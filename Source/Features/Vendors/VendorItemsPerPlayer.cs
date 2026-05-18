using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace ServerSideTweaks.Features.Vendors
{
    internal static class VendorItemsPerPlayer
    {
        private const float BossProgressCreditRadius = 64f;

        private static readonly Dictionary<string, HashSet<string>> ProgressByPlayer = new(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string>? _restrictedGlobalKeys;
        private static string? _restrictedGlobalKeysConfig;
        private static bool _progressLoaded;

        internal static void ClearRuntimeCache()
        {
            ProgressByPlayer.Clear();
            _restrictedGlobalKeys = null;
            _restrictedGlobalKeysConfig = null;
            _progressLoaded = false;
        }

        internal static bool TrySendFilteredGlobalKeys(ZoneSystem zoneSystem, long peer)
        {
            if (!IsEnabled())
            {
                return false;
            }

            try
            {
                EnsureProgressLoaded();

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
            EnsureProgressLoaded();

            if (players.Count == 0)
            {
                DebugLog($"No nearby players found for boss key {defeatKey}.");
                return;
            }

            bool changed = false;
            foreach (PlayerProgressTarget player in players)
            {
                string playerName = SanitizePlayerName(player.PlayerName);
                if (string.IsNullOrWhiteSpace(playerName))
                {
                    DebugLog($"Unable to record {defeatKey} for player {player.PlayerId}: player name is empty.");
                    continue;
                }

                HashSet<string> playerProgress = GetProgress(playerName);
                if (playerProgress.Add(defeatKey))
                {
                    changed = true;
                    DebugLog($"Recorded {defeatKey} for player {playerName} ({player.PlayerId}).");
                }
            }

            if (!changed)
            {
                return;
            }

            SaveProgress();
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
            string playerProgressKey = hasPlayerInfo ? SanitizePlayerName(playerName) : "";
            HashSet<string> progress = !string.IsNullOrWhiteSpace(playerProgressKey) ? GetProgress(playerProgressKey) : new HashSet<string>(StringComparer.Ordinal);
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
            DebugLog($"Sent {filteredKeys.Count}/{keys.Count} global key(s) to peer {peer.m_uid}; player={(hasPlayerInfo ? $"{playerName} ({playerId})" : "unknown")}.");
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

        private static HashSet<string> GetProgress(string playerKey)
        {
            string sanitized = SanitizePlayerName(playerKey);
            if (!ProgressByPlayer.TryGetValue(sanitized, out HashSet<string> progress))
            {
                progress = new HashSet<string>(StringComparer.Ordinal);
                ProgressByPlayer[sanitized] = progress;
            }

            return progress;
        }

        private static void EnsureProgressLoaded()
        {
            if (_progressLoaded)
            {
                return;
            }

            _progressLoaded = true;
            ProgressByPlayer.Clear();
            string path = GetProgressFilePath();

            if (!File.Exists(path))
            {
                return;
            }

            foreach (string line in File.ReadAllLines(path))
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
                    GetProgress(playerName).Add(keyName);
                }
            }
        }

        private static void SaveProgress()
        {
            string path = GetProgressFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Paths.ConfigPath);

            List<string> lines = new();
            foreach (KeyValuePair<string, HashSet<string>> playerEntry in ProgressByPlayer.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string key in playerEntry.Value.OrderBy(key => key, StringComparer.Ordinal))
                {
                    lines.Add($"{EscapeTsv(playerEntry.Key)}\t{key}");
                }
            }

            File.WriteAllLines(path, lines);
        }

        private static string GetProgressFilePath()
        {
            string configured = ModConfig.VendorProgressFile.Value.Trim();
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Paths.ConfigPath, configured);
        }

        private static void DebugLog(string message)
        {
            ServerSideTweaksPlugin.ModLogger.LogInfo(message);
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"{vector.x:0.0},{vector.y:0.0},{vector.z:0.0}";
        }

        private static string SanitizePlayerName(string value)
        {
            return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static string EscapeTsv(string value)
        {
            return SanitizePlayerName(value);
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
    }

    [HarmonyPatch(typeof(ZoneSystem), "SendGlobalKeys")]
    internal static class ZoneSystemSendGlobalKeysPatch
    {
        private static bool Prefix(ZoneSystem __instance, long peer)
        {
            return !VendorItemsPerPlayer.TrySendFilteredGlobalKeys(__instance, peer);
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
