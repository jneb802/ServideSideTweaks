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
        private static readonly Dictionary<string, HashSet<string>> ProgressByPlayer = new(StringComparer.Ordinal);
        private static readonly Dictionary<ZDOID, BossProgress> BossProgressByZdo = new();
        private static HashSet<string>? _restrictedGlobalKeys;
        private static string? _restrictedGlobalKeysConfig;
        private static bool _progressLoaded;

        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("RPC_Damage", HandleDamage);
            RoutedRpcDispatcher.Register("SetGlobalKey", HandleSetGlobalKey);
        }

        internal static void ClearRuntimeCache()
        {
            ProgressByPlayer.Clear();
            BossProgressByZdo.Clear();
            _restrictedGlobalKeys = null;
            _restrictedGlobalKeysConfig = null;
            _progressLoaded = false;
        }

        private static RoutedRpcAction HandleDamage(ZRoutedRpc.RoutedRPCData rpcData)
        {
            TrackBossDamage(rpcData);
            return RoutedRpcAction.Continue;
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

        internal static void TrackBossDamage(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!IsEnabled() || rpcData.m_targetZDO.IsNone())
            {
                return;
            }

            try
            {
                ZDO? target = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(rpcData.m_targetZDO) : null;
                Character? character = GetCharacter(target);
                if (character == null || !IsTrackedBoss(character))
                {
                    return;
                }

                HitData hit = ReadHitData(rpcData.m_parameters);
                if (hit.GetTotalDamage() <= 0.0f || !TryGetDamagePlayerId(rpcData.m_senderPeerID, hit, out long playerId))
                {
                    return;
                }

                string defeatKey = GetKeyName(character.m_defeatSetGlobalKey);
                if (!BossProgressByZdo.TryGetValue(rpcData.m_targetZDO, out BossProgress progress))
                {
                    progress = new BossProgress(defeatKey);
                    BossProgressByZdo[rpcData.m_targetZDO] = progress;
                }

                progress.PlayerIds.Add(playerId);
                DebugLog($"Tracked boss damage for {defeatKey} from player {playerId}.");
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to track boss damage for vendor progress: {ex}");
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

                HashSet<long> playerIds = GetTrackedPlayers(defeatKey);
                if (playerIds.Count == 0 && TryGetSenderPlayerId(rpcData.m_senderPeerID, out long senderPlayerId))
                {
                    playerIds.Add(senderPlayerId);
                    DebugLog($"Crediting {defeatKey} to SetGlobalKey sender player {senderPlayerId}.");
                }

                RecordBossProgress(defeatKey, playerIds);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to record boss global key for vendor progress: {ex}");
            }
        }

        internal static void TrackBossDeath(Character character)
        {
            if (!IsEnabled() || !IsTrackedBoss(character))
            {
                return;
            }

            try
            {
                ZDOID bossId = character.GetZDOID();
                string defeatKey = GetKeyName(character.m_defeatSetGlobalKey);
                RecordBossProgress(defeatKey, GetTrackedPlayers(defeatKey, bossId));
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to record boss death for vendor progress: {ex}");
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

        private static HashSet<long> GetTrackedPlayers(string defeatKey, ZDOID specificBossId = default)
        {
            HashSet<long> playerIds = new();
            List<ZDOID> completedBosses = new();

            foreach (KeyValuePair<ZDOID, BossProgress> entry in BossProgressByZdo)
            {
                if ((!specificBossId.IsNone() && entry.Key != specificBossId) ||
                    !string.Equals(entry.Value.DefeatKey, defeatKey, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (long playerId in entry.Value.PlayerIds)
                {
                    playerIds.Add(playerId);
                }

                completedBosses.Add(entry.Key);
            }

            foreach (ZDOID bossId in completedBosses)
            {
                BossProgressByZdo.Remove(bossId);
            }

            return playerIds;
        }

        private static bool IsEnabled()
        {
            return ModConfig.EnableVendorItemsPerPlayer.Value == true &&
                ZNet.instance != null &&
                ZNet.instance.IsServer() &&
                ZRoutedRpc.instance != null;
        }

        private static bool TryGetDamagePlayerId(long sender, HitData hit, out long playerId)
        {
            playerId = GetPlayerId(hit.m_attacker);
            if (playerId != 0L)
            {
                return true;
            }

            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null)
            {
                return false;
            }

            playerId = GetPlayerId(peer.m_characterID);
            return playerId != 0L;
        }

        private static bool IsTrackedBoss(Character character)
        {
            if (character == null || !character.IsBoss() || string.IsNullOrWhiteSpace(character.m_defeatSetGlobalKey))
            {
                return false;
            }

            return GetRestrictedGlobalKeys().Contains(GetKeyName(character.m_defeatSetGlobalKey));
        }

        private static Character? GetCharacter(ZDO? zdo)
        {
            if (zdo == null || ZNetScene.instance == null)
            {
                return null;
            }

            ZNetView? instance = ZNetScene.instance.FindInstance(zdo);
            if (instance != null)
            {
                Character? character = instance.GetComponent<Character>();
                if (character != null)
                {
                    return character;
                }
            }

            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            return prefab != null ? prefab.GetComponent<Character>() : null;
        }

        private static HitData ReadHitData(ZPackage parameters)
        {
            parameters.SetPos(0);
            HitData hit = new();
            hit.Deserialize(ref parameters);
            parameters.SetPos(0);
            return hit;
        }

        private static string ReadGlobalKey(ZPackage parameters)
        {
            parameters.SetPos(0);
            string key = GetKeyName(parameters.ReadString());
            parameters.SetPos(0);
            return key;
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
            playerId = GetPlayerId(peer.m_characterID);
            return playerId != 0L;
        }

        private static bool TryGetSenderPlayerId(long sender, out long playerId)
        {
            playerId = 0L;

            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null)
            {
                return false;
            }

            playerId = GetPlayerId(peer.m_characterID);
            return playerId != 0L;
        }

        private static long GetPlayerId(ZDOID characterId)
        {
            if (characterId.IsNone() || ZDOMan.instance == null)
            {
                return 0L;
            }

            ZDO? playerZdo = ZDOMan.instance.GetZDO(characterId);
            if (playerZdo == null)
            {
                return 0L;
            }

            return playerZdo.GetLong(ZDOVars.s_playerID);
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

        private sealed class BossProgress
        {
            internal BossProgress(string defeatKey)
            {
                DefeatKey = defeatKey;
            }

            internal string DefeatKey { get; }
            internal HashSet<long> PlayerIds { get; } = new();
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

    [HarmonyPatch(typeof(Character), "OnDeath")]
    internal static class CharacterOnDeathVendorProgressPatch
    {
        private static void Prefix(Character __instance)
        {
            VendorItemsPerPlayer.TrackBossDeath(__instance);
        }
    }
}
