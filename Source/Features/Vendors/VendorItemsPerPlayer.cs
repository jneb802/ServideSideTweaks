using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks.Features.Vendors
{
    internal static class VendorItemsPerPlayer
    {
        private const float BossProgressCreditRadius = 64f;

        private static readonly Dictionary<string, HashSet<string>> ProgressByPlayer = new(StringComparer.Ordinal);
        private static HashSet<string>? _restrictedGlobalKeys;
        private static string? _restrictedGlobalKeysConfig;
        private static bool _progressLoaded;

        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("SetGlobalKey", HandleSetGlobalKey);
        }

        internal static void ClearRuntimeCache()
        {
            ProgressByPlayer.Clear();
            _restrictedGlobalKeys = null;
            _restrictedGlobalKeysConfig = null;
            _progressLoaded = false;
        }

        private static RoutedRpcAction HandleSetGlobalKey(ZRoutedRpc.RoutedRPCData rpcData)
        {
            TrackBossDefeatGlobalKey(rpcData);
            return RoutedRpcAction.Continue;
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

                HashSet<long> playerIds = GetNearbyPlayerIds(rpcData.m_senderPeerID, BossProgressCreditRadius, defeatKey);
                RecordBossProgress(defeatKey, playerIds);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to record boss global key for vendor progress: {ex}");
            }
        }

        private static void RecordBossProgress(string defeatKey, HashSet<long> playerIds)
        {
            EnsureProgressLoaded();

            if (playerIds.Count == 0)
            {
                DebugLog($"No player damage records found for boss key {defeatKey}.");
                return;
            }

            bool changed = false;
            foreach (long playerId in playerIds)
            {
                string playerKey = playerId.ToString();
                HashSet<string> playerProgress = GetProgress(playerKey);
                if (playerProgress.Add(defeatKey))
                {
                    changed = true;
                    DebugLog($"Recorded {defeatKey} for player {playerKey}.");
                }
            }

            if (!changed)
            {
                return;
            }

            SaveProgress();
            SendGlobalKeysToConnectedPeers(ZoneSystem.instance);
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

        private static HashSet<long> GetNearbyPlayerIds(long sender, float radius, string defeatKey)
        {
            HashSet<long> playerIds = new();
            if (!TryGetPeerPlayerInfo(sender, out long senderPlayerId, out Vector3 origin))
            {
                DebugLog($"Unable to credit {defeatKey}: SetGlobalKey sender {sender} has no resolved player position.");
                return playerIds;
            }

            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer == null || !peer.IsReady() || !TryGetPeerPlayerInfo(peer, out long playerId, out Vector3 position))
                {
                    continue;
                }

                if (Vector3.Distance(origin, position) <= radius)
                {
                    playerIds.Add(playerId);
                }
            }

            DebugLog($"Crediting {defeatKey} to {playerIds.Count} player(s) within {radius:0.#}m of sender player {senderPlayerId} at {FormatVector(origin)}.");
            return playerIds;
        }

        private static void SendGlobalKeysToConnectedPeers(ZoneSystem zoneSystem)
        {
            if (zoneSystem == null)
            {
                return;
            }

            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer != null && peer.IsReady())
                {
                    SendGlobalKeys(zoneSystem, peer);
                }
            }
        }

        private static void SendGlobalKeys(ZoneSystem zoneSystem, ZNetPeer peer)
        {
            List<string> keys = zoneSystem.GetGlobalKeys();
            bool hasPlayerId = TryGetPeerPlayerId(peer, out long playerId);
            HashSet<string> progress = hasPlayerId ? GetProgress(playerId.ToString()) : new HashSet<string>(StringComparer.Ordinal);
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
            DebugLog($"Sent {filteredKeys.Count}/{keys.Count} global key(s) to peer {peer.m_uid}; playerID={(hasPlayerId ? playerId.ToString() : "unknown")}.");
        }

        private static bool TryGetPeerPlayerId(ZNetPeer peer, out long playerId)
        {
            return TryGetPeerPlayerInfo(peer, out playerId, out _);
        }

        private static bool TryGetPeerPlayerInfo(long peerId, out long playerId, out Vector3 position)
        {
            ZNetPeer peer = ZNet.instance.GetPeer(peerId);
            if (peer == null)
            {
                playerId = 0L;
                position = Vector3.zero;
                return false;
            }

            return TryGetPeerPlayerInfo(peer, out playerId, out position);
        }

        private static bool TryGetPeerPlayerInfo(ZNetPeer peer, out long playerId, out Vector3 position)
        {
            playerId = 0L;
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
            if (!ProgressByPlayer.TryGetValue(playerKey, out HashSet<string> progress))
            {
                progress = new HashSet<string>(StringComparer.Ordinal);
                ProgressByPlayer[playerKey] = progress;
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

                string keyName = GetKeyName(parts[1]);
                if (GetRestrictedGlobalKeys().Contains(keyName))
                {
                    GetProgress(parts[0].Trim()).Add(keyName);
                }
            }
        }

        private static void SaveProgress()
        {
            string path = GetProgressFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Paths.ConfigPath);

            List<string> lines = new();
            foreach (KeyValuePair<string, HashSet<string>> playerEntry in ProgressByPlayer.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                foreach (string key in playerEntry.Value.OrderBy(key => key, StringComparer.Ordinal))
                {
                    lines.Add($"{playerEntry.Key}\t{key}");
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
            if (ModConfig.DebugVendorItemsPerPlayer.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"{vector.x:0.0},{vector.y:0.0},{vector.z:0.0}";
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
}
