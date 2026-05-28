using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace ServerSideTweaks.Features.Vendors
{
    internal static class VendorItemsPerPlayer
    {
        private const float BossProgressCreditRadius = 64f;
        private const float GlobalKeyRetryDelaySeconds = 0.5f;
        private const int GlobalKeyRetryAttempts = 240;
        private const string DefaultProgressFileName = "warpalicious.serverSideTweaks.vendorProgress.yaml";
        private const string LegacyProgressFileName = "warpalicious.serverSideTweaks.vendorProgress.tsv";

        private static readonly Dictionary<long, PendingGlobalKeyRetry> _pendingGlobalKeyRetries = new();
        private static HashSet<string>? _restrictedGlobalKeys;
        private static string? _restrictedGlobalKeysConfig;

        internal static void ClearRuntimeCache()
        {
            _pendingGlobalKeyRetries.Clear();
            _restrictedGlobalKeys = null;
            _restrictedGlobalKeysConfig = null;
        }

        internal static void Update()
        {
            if (!IsEnabled() || ZoneSystem.instance == null || _pendingGlobalKeyRetries.Count == 0)
            {
                return;
            }

            float now = Time.time;
            List<long> duePeerIds = new();
            foreach (KeyValuePair<long, PendingGlobalKeyRetry> retryEntry in _pendingGlobalKeyRetries)
            {
                if (retryEntry.Value.NextRetryTime <= now)
                {
                    duePeerIds.Add(retryEntry.Key);
                }
            }

            foreach (long peerId in duePeerIds)
            {
                if (!_pendingGlobalKeyRetries.TryGetValue(peerId, out PendingGlobalKeyRetry retry))
                {
                    continue;
                }

                ZNetPeer peer = ZNet.instance.GetPeer(peerId);
                if (peer == null || !peer.IsReady())
                {
                    _pendingGlobalKeyRetries.Remove(peerId);
                    continue;
                }

                if (TryGetPeerPlayerInfo(peer, out _, out _, out _))
                {
                    SendGlobalKeys(ZoneSystem.instance, peer);
                    continue;
                }

                retry.AttemptsRemaining--;
                if (retry.AttemptsRemaining <= 0)
                {
                    _pendingGlobalKeyRetries.Remove(peerId);
                    DebugLog($"Stopped retrying vendor global key sync for peer {peerId}: player info did not become available after {GlobalKeyRetryAttempts} attempts.");
                    continue;
                }

                retry.NextRetryTime = now + GlobalKeyRetryDelaySeconds;
                _pendingGlobalKeyRetries[peerId] = retry;
            }
        }

        internal static bool TrySendFilteredGlobalKeys(ZoneSystem zoneSystem, long peer)
        {
            if (!IsEnabled())
            {
                return false;
            }

            try
            {
                if (peer == ZRoutedRpc.Everybody)
                {
                    foreach (ZNetPeer connectedPeer in ZNet.instance.GetConnectedPeers())
                    {
                        if (connectedPeer != null && connectedPeer.IsReady())
                        {
                            SendGlobalKeys(zoneSystem, connectedPeer);
                        }
                    }

                    return true;
                }

                ZNetPeer targetPeer = ZNet.instance.GetPeer(peer);
                if (targetPeer != null && targetPeer.IsReady())
                {
                    SendGlobalKeys(zoneSystem, targetPeer);
                }

                return true;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to send per-player vendor global keys: {ex}");
                return true;
            }
        }

        internal static void TrackBossDefeatGlobalKey(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!IsEnabled())
            {
                return;
            }

            try
            {
                string defeatKey = ReadGlobalKey(rpcData.m_parameters);
                if (!GetRestrictedGlobalKeys().Contains(defeatKey))
                {
                    return;
                }

                List<PlayerProgressTarget> players = GetNearbyPlayers(rpcData.m_senderPeerID, BossProgressCreditRadius, defeatKey);
                RecordBossProgress(defeatKey, players);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to record boss global key for vendor progress: {ex}");
            }
        }

        private static void RecordBossProgress(string defeatKey, List<PlayerProgressTarget> players)
        {
            if (players.Count == 0)
            {
                DebugLog($"No nearby players found for boss key {defeatKey}.");
                return;
            }

            VendorProgressDocument progressDocument = LoadProgressDocument();
            bool changed = false;
            foreach (PlayerProgressTarget player in players)
            {
                string playerName = SanitizePlayerName(player.PlayerName);
                if (string.IsNullOrWhiteSpace(playerName))
                {
                    DebugLog($"Unable to record {defeatKey} for player {player.PlayerId}: player name is empty.");
                    continue;
                }

                PlayerVendorProgress playerProgress = progressDocument.GetOrCreatePlayer(playerName);
                if (player.PlayerId != 0L && playerProgress.PlayerId != player.PlayerId)
                {
                    playerProgress.PlayerId = player.PlayerId;
                    changed = true;
                }

                if (playerProgress.GlobalKeys.Add(defeatKey))
                {
                    changed = true;
                    DebugLog($"Recorded {defeatKey} for player {playerName} ({player.PlayerId}).");
                }
            }

            if (!changed)
            {
                return;
            }

            SaveProgressDocument(progressDocument);
        }

        private static bool IsEnabled()
        {
            return ModConfig.EnableVendorItemsPerPlayer.Value == true &&
                ZNet.instance != null &&
                ZNet.instance.IsServer() &&
                ZRoutedRpc.instance != null;
        }

        private static string ReadGlobalKey(ZPackage parameters)
        {
            parameters.SetPos(0);
            string key = GetKeyName(parameters.ReadString());
            parameters.SetPos(0);
            return key;
        }

        private static List<PlayerProgressTarget> GetNearbyPlayers(long sender, float radius, string defeatKey)
        {
            List<PlayerProgressTarget> players = new();
            HashSet<long> addedPlayerIds = new();
            if (!TryGetPeerPlayerInfo(sender, out long senderPlayerId, out string senderPlayerName, out Vector3 origin))
            {
                DebugLog($"Unable to credit {defeatKey}: SetGlobalKey sender {sender} has no resolved player position.");
                return players;
            }

            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer == null || !peer.IsReady() || !TryGetPeerPlayerInfo(peer, out long playerId, out string playerName, out Vector3 position))
                {
                    continue;
                }

                if (Vector3.Distance(origin, position) <= radius && addedPlayerIds.Add(playerId))
                {
                    players.Add(new PlayerProgressTarget(playerId, playerName));
                }
            }

            DebugLog($"Crediting {defeatKey} to {players.Count} player(s) within {radius:0.#}m of sender player {senderPlayerName} ({senderPlayerId}) at {FormatVector(origin)}.");
            return players;
        }

        private static void SendGlobalKeys(ZoneSystem zoneSystem, ZNetPeer peer)
        {
            List<string> keys = zoneSystem.GetGlobalKeys();
            bool hasPlayerInfo = TryGetPeerPlayerInfo(peer, out long playerId, out string playerName, out _);
            bool hasProgressName = hasPlayerInfo;
            if (!hasProgressName)
            {
                hasProgressName = TryGetPeerProgressName(peer, out playerName);
            }

            if (hasProgressName)
            {
                _pendingGlobalKeyRetries.Remove(peer.m_uid);
            }
            else
            {
                QueueGlobalKeyRetry(peer.m_uid, false);
            }

            string playerProgressKey = hasProgressName ? SanitizePlayerName(playerName) : "";
            HashSet<string> progress = !string.IsNullOrWhiteSpace(playerProgressKey)
                ? LoadProgressDocument().GetPlayerKeys(playerProgressKey)
                : new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> restrictedKeys = GetRestrictedGlobalKeys();
            List<string> filteredKeys = new();

            foreach (string key in keys)
            {
                string keyName = GetKeyName(key);
                if (!restrictedKeys.Contains(keyName) || progress.Contains(keyName))
                {
                    filteredKeys.Add(key);
                }
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "GlobalKeys", filteredKeys);
            DebugLog($"Sent {filteredKeys.Count}/{keys.Count} global key(s) to peer {peer.m_uid}; player={FormatPlayerForLog(hasPlayerInfo, hasProgressName, playerName, playerId)}.");
        }

        internal static void QueueGlobalKeyRetryForCharacter(ZDOID characterId)
        {
            if (!IsEnabled() || characterId.IsNone())
            {
                return;
            }

            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer != null && peer.IsReady() && peer.m_characterID == characterId)
                {
                    QueueGlobalKeyRetry(peer.m_uid, true);
                    return;
                }
            }
        }

        private static void QueueGlobalKeyRetry(long peerId, bool retryImmediately)
        {
            if (_pendingGlobalKeyRetries.TryGetValue(peerId, out PendingGlobalKeyRetry existingRetry) &&
                existingRetry.AttemptsRemaining >= GlobalKeyRetryAttempts / 2)
            {
                return;
            }

            _pendingGlobalKeyRetries[peerId] = new PendingGlobalKeyRetry
            {
                AttemptsRemaining = GlobalKeyRetryAttempts,
                NextRetryTime = retryImmediately ? Time.time : Time.time + GlobalKeyRetryDelaySeconds
            };
            DebugLog($"Queued vendor global key retry for peer {peerId}: player info is not available yet.");
        }

        private static bool TryGetPeerPlayerInfo(long peerId, out long playerId, out string playerName, out Vector3 position)
        {
            ZNetPeer peer = ZNet.instance.GetPeer(peerId);
            if (peer == null)
            {
                playerId = 0L;
                playerName = "";
                position = Vector3.zero;
                return false;
            }

            return TryGetPeerPlayerInfo(peer, out playerId, out playerName, out position);
        }

        private static bool TryGetPeerProgressName(ZNetPeer peer, out string playerName)
        {
            playerName = "";

            if (!peer.m_characterID.IsNone() && ZDOMan.instance != null)
            {
                ZDO? playerZdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (playerZdo != null)
                {
                    playerName = SanitizePlayerName(playerZdo.GetString(ZDOVars.s_playerName, ""));
                }
            }

            if (string.IsNullOrWhiteSpace(playerName))
            {
                playerName = SanitizePlayerName(peer.m_playerName ?? "");
            }

            return !string.IsNullOrWhiteSpace(playerName);
        }

        private static bool TryGetPeerPlayerInfo(ZNetPeer peer, out long playerId, out string playerName, out Vector3 position)
        {
            playerId = 0L;
            playerName = "";
            position = Vector3.zero;

            if (peer.m_characterID.IsNone() || ZDOMan.instance == null)
            {
                return false;
            }

            ZDO? playerZdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (playerZdo == null)
            {
                return false;
            }

            playerId = playerZdo.GetLong(ZDOVars.s_playerID);
            if (playerId == 0L)
            {
                return false;
            }

            playerName = SanitizePlayerName(playerZdo.GetString(ZDOVars.s_playerName, ""));
            if (string.IsNullOrWhiteSpace(playerName))
            {
                playerName = peer.m_playerName ?? "";
            }

            if (string.IsNullOrWhiteSpace(playerName))
            {
                return false;
            }

            position = playerZdo.GetPosition();
            return true;
        }

        private static HashSet<string> GetRestrictedGlobalKeys()
        {
            string configured = ModConfig.VendorProgressGlobalKeys.Value;
            if (_restrictedGlobalKeys != null && string.Equals(_restrictedGlobalKeysConfig, configured, StringComparison.Ordinal))
            {
                return _restrictedGlobalKeys;
            }

            _restrictedGlobalKeysConfig = configured;
            _restrictedGlobalKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in configured.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string keyName = GetKeyName(key);
                if (!string.IsNullOrWhiteSpace(keyName))
                {
                    _restrictedGlobalKeys.Add(keyName);
                }
            }

            return _restrictedGlobalKeys;
        }

        private static string GetKeyName(string key)
        {
            return key.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        }

        private static VendorProgressDocument LoadProgressDocument()
        {
            string path = GetProgressFilePath();
            if (File.Exists(path))
            {
                return ParseYamlProgress(File.ReadAllLines(path));
            }

            string legacyPath = GetLegacyProgressFilePath();
            if (File.Exists(legacyPath))
            {
                return ParseLegacyTsvProgress(File.ReadAllLines(legacyPath));
            }

            return new VendorProgressDocument();
        }

        private static VendorProgressDocument ParseLegacyTsvProgress(IEnumerable<string> lines)
        {
            VendorProgressDocument progressDocument = new();
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = line.Split(new[] { '\t' }, 2);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                {
                    continue;
                }

                string playerName = SanitizePlayerName(parts[0]);
                string keyName = GetKeyName(parts[1]);
                if (!string.IsNullOrWhiteSpace(playerName) && GetRestrictedGlobalKeys().Contains(keyName))
                {
                    progressDocument.GetOrCreatePlayer(playerName).GlobalKeys.Add(keyName);
                }
            }

            return progressDocument;
        }

        private static VendorProgressDocument ParseYamlProgress(IEnumerable<string> lines)
        {
            VendorProgressDocument progressDocument = new();
            PlayerVendorProgress? currentPlayer = null;
            bool inPlayers = false;
            bool inGlobalKeys = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd();
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int indent = CountLeadingSpaces(line);
                if (indent == 0)
                {
                    inPlayers = string.Equals(trimmed, "players:", StringComparison.Ordinal);
                    currentPlayer = null;
                    inGlobalKeys = false;
                    continue;
                }

                if (!inPlayers)
                {
                    continue;
                }

                if (indent == 2 && trimmed.EndsWith(":", StringComparison.Ordinal))
                {
                    string playerName = SanitizePlayerName(UnquoteYamlScalar(trimmed.Substring(0, trimmed.Length - 1)));
                    currentPlayer = !string.IsNullOrWhiteSpace(playerName)
                        ? progressDocument.GetOrCreatePlayer(playerName)
                        : null;
                    inGlobalKeys = false;
                    continue;
                }

                if (currentPlayer == null)
                {
                    continue;
                }

                if (indent == 4)
                {
                    inGlobalKeys = false;
                    if (trimmed.StartsWith("playerId:", StringComparison.Ordinal))
                    {
                        string rawPlayerId = trimmed.Substring("playerId:".Length).Trim();
                        if (long.TryParse(rawPlayerId, out long playerId))
                        {
                            currentPlayer.PlayerId = playerId;
                        }
                    }
                    else if (trimmed.StartsWith("globalKeys:", StringComparison.Ordinal))
                    {
                        inGlobalKeys = true;
                    }

                    continue;
                }

                if (inGlobalKeys && indent == 6 && trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    string keyName = GetKeyName(UnquoteYamlScalar(trimmed.Substring(2).Trim()));
                    if (GetRestrictedGlobalKeys().Contains(keyName))
                    {
                        currentPlayer.GlobalKeys.Add(keyName);
                    }
                }
            }

            return progressDocument;
        }

        private static void SaveProgressDocument(VendorProgressDocument progressDocument)
        {
            string path = GetProgressFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Paths.ConfigPath);

            string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, RenderYamlProgress(progressDocument));
            ReplaceProgressFile(tempPath, path);
        }

        private static void ReplaceProgressFile(string tempPath, string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                }
                catch (IOException)
                {
                }

                File.Copy(tempPath, path, true);
                File.Delete(tempPath);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }

        private static string RenderYamlProgress(VendorProgressDocument progressDocument)
        {
            StringBuilder builder = new();
            builder.AppendLine("# serverSideTweaks vendor progress. Manual edits are read without restarting the server.");
            builder.AppendLine("players:");

            foreach (KeyValuePair<string, PlayerVendorProgress> playerEntry in progressDocument.Players.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("  ").Append(QuoteYamlScalar(playerEntry.Key)).AppendLine(":");
                builder.Append("    playerId: ").Append(playerEntry.Value.PlayerId).AppendLine();
                builder.AppendLine("    globalKeys:");
                foreach (string key in playerEntry.Value.GlobalKeys.OrderBy(key => key, StringComparer.Ordinal))
                {
                    builder.Append("      - ").Append(FormatGlobalKey(key)).AppendLine();
                }
            }

            return builder.ToString();
        }

        private static string GetProgressFilePath()
        {
            string configured = ModConfig.VendorProgressFile.Value.Trim();
            if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, LegacyProgressFileName, StringComparison.OrdinalIgnoreCase))
            {
                configured = DefaultProgressFileName;
            }

            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Paths.ConfigPath, configured);
        }

        private static string GetLegacyProgressFilePath()
        {
            return Path.Combine(Paths.ConfigPath, LegacyProgressFileName);
        }

        private static void DebugLog(string message)
        {
            ServerSideTweaksPlugin.ModLogger.LogInfo(message);
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"{vector.x:0.0},{vector.y:0.0},{vector.z:0.0}";
        }

        private static string FormatPlayerForLog(bool hasPlayerInfo, bool hasProgressName, string playerName, long playerId)
        {
            if (hasPlayerInfo)
            {
                return $"{playerName} ({playerId})";
            }

            return hasProgressName ? $"{playerName} (pending character)" : "unknown";
        }

        private static string SanitizePlayerName(string value)
        {
            return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static int CountLeadingSpaces(string value)
        {
            int count = 0;
            while (count < value.Length && value[count] == ' ')
            {
                count++;
            }

            return count;
        }

        private static string QuoteYamlScalar(string value)
        {
            return $"'{SanitizePlayerName(value).Replace("'", "''")}'";
        }

        private static string FormatGlobalKey(string value)
        {
            return GetKeyName(value);
        }

        private static string UnquoteYamlScalar(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\'')
            {
                return trimmed.Substring(1, trimmed.Length - 2).Replace("''", "'");
            }

            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                return trimmed.Substring(1, trimmed.Length - 2).Replace("\\\"", "\"");
            }

            return trimmed;
        }

        private readonly struct PlayerProgressTarget
        {
            internal PlayerProgressTarget(long playerId, string playerName)
            {
                PlayerId = playerId;
                PlayerName = SanitizePlayerName(playerName);
            }

            internal long PlayerId { get; }
            internal string PlayerName { get; }
        }

        private sealed class VendorProgressDocument
        {
            internal Dictionary<string, PlayerVendorProgress> Players { get; } = new(StringComparer.OrdinalIgnoreCase);

            internal PlayerVendorProgress GetOrCreatePlayer(string playerName)
            {
                string sanitized = SanitizePlayerName(playerName);
                if (!Players.TryGetValue(sanitized, out PlayerVendorProgress progress))
                {
                    progress = new PlayerVendorProgress();
                    Players[sanitized] = progress;
                }

                return progress;
            }

            internal HashSet<string> GetPlayerKeys(string playerName)
            {
                string sanitized = SanitizePlayerName(playerName);
                return Players.TryGetValue(sanitized, out PlayerVendorProgress progress)
                    ? new HashSet<string>(progress.GlobalKeys, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private sealed class PlayerVendorProgress
        {
            internal long PlayerId { get; set; }
            internal HashSet<string> GlobalKeys { get; } = new(StringComparer.Ordinal);
        }

        private struct PendingGlobalKeyRetry
        {
            internal int AttemptsRemaining { get; set; }
            internal float NextRetryTime { get; set; }
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "SendGlobalKeys")]
    internal static class ZoneSystemSendGlobalKeysPatch
    {
        private static bool Prefix(ZoneSystem __instance, long peer)
        {
            return !VendorItemsPerPlayer.TrySendFilteredGlobalKeys(__instance, peer);
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
    internal static class ZNetCharacterIdVendorGlobalKeysPatch
    {
        private static void Postfix(ZDOID characterID)
        {
            VendorItemsPerPlayer.QueueGlobalKeyRetryForCharacter(characterID);
        }
    }

    [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
    internal static class ZRoutedRpcHandleRoutedRpcVendorProgressPatch
    {
        private static readonly int SetGlobalKeyHash = "SetGlobalKey".GetStableHashCode();

        private static void Prefix(ZRoutedRpc.RoutedRPCData data)
        {
            if (data.m_methodHash == SetGlobalKeyHash)
            {
                VendorItemsPerPlayer.TrackBossDefeatGlobalKey(data);
            }
        }
    }
}
