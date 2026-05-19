using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace ServerSideTweaks.Features.Locations
{
    internal static class PerPlayerLocationIcons
    {
        private static readonly Dictionary<Vector2i, List<LocationIconCandidate>> CandidatesByPlayerZone = new();
        private static readonly Dictionary<string, HashSet<string>> DiscoveriesByPlayer = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<long, Vector2i> LastCheckedZoneByPeer = new();

        private static bool _indexBuilt;
        private static bool _discoveriesLoaded;
        private static float _indexedRevealDistance = -1.0f;

        internal static void ClearRuntimeCache()
        {
            CandidatesByPlayerZone.Clear();
            DiscoveriesByPlayer.Clear();
            LastCheckedZoneByPeer.Clear();
            _indexBuilt = false;
            _discoveriesLoaded = false;
            _indexedRevealDistance = -1.0f;
        }

        internal static bool TrySendFilteredLocationIcons(ZoneSystem zoneSystem, long peer)
        {
            if (!IsEnabled())
            {
                return false;
            }

            try
            {
                EnsureDiscoveryStateLoaded();
                EnsureCandidateIndex(zoneSystem);

                if (peer == ZRoutedRpc.Everybody)
                {
                    foreach (ZNetPeer connectedPeer in ZNet.instance.GetConnectedPeers())
                    {
                        if (connectedPeer != null && connectedPeer.IsReady())
                        {
                            SendLocationIcons(zoneSystem, connectedPeer);
                        }
                    }

                    return true;
                }

                ZNetPeer targetPeer = ZNet.instance.GetPeer(peer);
                if (targetPeer != null && targetPeer.IsReady())
                {
                    SendLocationIcons(zoneSystem, targetPeer);
                }

                return true;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to send per-player location icons: {ex}");
                return false;
            }
        }

        internal static void OnServerSyncedPlayerData(ZRpc rpc)
        {
            if (!IsEnabled() || ZoneSystem.instance == null)
            {
                return;
            }

            try
            {
                ZNetPeer? peer = FindPeer(rpc);
                if (peer == null || !peer.IsReady())
                {
                    return;
                }

                Vector3 playerPosition = peer.GetRefPos();
                Vector2i playerZone = ZoneSystem.GetZone(playerPosition);
                if (LastCheckedZoneByPeer.TryGetValue(peer.m_uid, out Vector2i lastZone) && lastZone == playerZone)
                {
                    return;
                }

                EnsureDiscoveryStateLoaded();
                EnsureCandidateIndex(ZoneSystem.instance);

                if (!CandidatesByPlayerZone.TryGetValue(playerZone, out List<LocationIconCandidate> candidates))
                {
                    LastCheckedZoneByPeer[peer.m_uid] = playerZone;
                    return;
                }

                PlayerIdentity identity = GetPlayerIdentity(peer);
                if (string.IsNullOrWhiteSpace(identity.PlayerName))
                {
                    DebugLog($"Skipped location icon discovery for peer {peer.m_uid}: player name is empty.");
                    return;
                }

                LastCheckedZoneByPeer[peer.m_uid] = playerZone;
                HashSet<string> discoveries = GetDiscoveries(identity.PlayerName);
                float revealDistance = GetRevealDistance();
                bool changed = false;

                foreach (LocationIconCandidate candidate in candidates)
                {
                    if (DistanceXZ(playerPosition, candidate.Position) > revealDistance)
                    {
                        continue;
                    }

                    if (discoveries.Add(candidate.Key))
                    {
                        changed = true;
                        DebugLog($"Discovered location icon {candidate.Key} for {identity.PlayerName} ({identity.PlayerId}) at {FormatVector(playerPosition)}.");
                    }
                }

                if (!changed)
                {
                    return;
                }

                SaveDiscoveryState();
                SendLocationIcons(ZoneSystem.instance, peer);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to process per-player location icon discovery: {ex}");
            }
        }

        private static bool IsEnabled()
        {
            return ModConfig.EnablePerPlayerLocationIcons.Value == true &&
                ZNet.instance != null &&
                ZNet.instance.IsServer() &&
                ZRoutedRpc.instance != null;
        }

        private static void EnsureCandidateIndex(ZoneSystem zoneSystem)
        {
            float revealDistance = GetRevealDistance();
            if (_indexBuilt && Mathf.Approximately(_indexedRevealDistance, revealDistance))
            {
                return;
            }

            RebuildCandidateIndex(zoneSystem);
        }

        private static void RebuildCandidateIndex(ZoneSystem zoneSystem)
        {
            CandidatesByPlayerZone.Clear();
            float revealDistance = GetRevealDistance();

            foreach (KeyValuePair<Vector2i, ZoneSystem.LocationInstance> entry in zoneSystem.m_locationInstances)
            {
                ZoneSystem.LocationInstance instance = entry.Value;
                if (!TryGetLocationIconName(instance, out string iconName))
                {
                    continue;
                }

                if (!IsPlacedRevealableIcon(instance))
                {
                    continue;
                }

                LocationIconCandidate candidate = new(
                    BuildLocationKey(entry.Key, iconName),
                    instance.m_position,
                    iconName);

                AddCandidateZones(candidate, revealDistance);
            }

            _indexBuilt = true;
            _indexedRevealDistance = revealDistance;
            LastCheckedZoneByPeer.Clear();
            DebugLog($"Rebuilt location icon candidate index with {CandidatesByPlayerZone.Count} player zone(s).");
        }

        private static void AddCandidateZones(LocationIconCandidate candidate, float revealDistance)
        {
            Vector3 min = candidate.Position + new Vector3(-revealDistance, 0.0f, -revealDistance);
            Vector3 max = candidate.Position + new Vector3(revealDistance, 0.0f, revealDistance);
            Vector2i minZone = ZoneSystem.GetZone(min);
            Vector2i maxZone = ZoneSystem.GetZone(max);

            for (int y = minZone.y; y <= maxZone.y; y++)
            {
                for (int x = minZone.x; x <= maxZone.x; x++)
                {
                    Vector2i playerZone = new(x, y);
                    if (!CandidatesByPlayerZone.TryGetValue(playerZone, out List<LocationIconCandidate> candidates))
                    {
                        candidates = new List<LocationIconCandidate>();
                        CandidatesByPlayerZone[playerZone] = candidates;
                    }

                    candidates.Add(candidate);
                }
            }
        }

        private static void SendLocationIcons(ZoneSystem zoneSystem, ZNetPeer peer)
        {
            PlayerIdentity identity = GetPlayerIdentity(peer);
            HashSet<string> discoveries = !string.IsNullOrWhiteSpace(identity.PlayerName)
                ? GetDiscoveries(identity.PlayerName)
                : new HashSet<string>(StringComparer.Ordinal);
            List<LocationIconCandidate> icons = new();

            foreach (KeyValuePair<Vector2i, ZoneSystem.LocationInstance> entry in zoneSystem.m_locationInstances)
            {
                ZoneSystem.LocationInstance instance = entry.Value;
                if (!TryGetLocationIconName(instance, out string iconName))
                {
                    continue;
                }

                if (instance.m_location.m_iconAlways)
                {
                    icons.Add(new LocationIconCandidate("", instance.m_position, iconName));
                    continue;
                }

                if (!IsPlacedRevealableIcon(instance))
                {
                    continue;
                }

                string locationKey = BuildLocationKey(entry.Key, iconName);
                if (discoveries.Contains(locationKey))
                {
                    icons.Add(new LocationIconCandidate(locationKey, instance.m_position, iconName));
                }
            }

            ZPackage pkg = new();
            pkg.Write(icons.Count);
            foreach (LocationIconCandidate icon in icons)
            {
                pkg.Write(icon.Position);
                pkg.Write(icon.IconName);
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "LocationIcons", pkg);
            DebugLog($"Sent {icons.Count} location icon(s) to peer {peer.m_uid}; player={(string.IsNullOrWhiteSpace(identity.PlayerName) ? "unknown" : $"{identity.PlayerName} ({identity.PlayerId})")}.");
        }

        private static bool IsPlacedRevealableIcon(ZoneSystem.LocationInstance instance)
        {
            return instance.m_placed && instance.m_location != null && instance.m_location.m_iconPlaced;
        }

        private static bool TryGetLocationIconName(ZoneSystem.LocationInstance instance, out string iconName)
        {
            iconName = "";
            if (instance.m_location == null || instance.m_location.m_prefab == null || string.IsNullOrWhiteSpace(instance.m_location.m_prefab.Name))
            {
                return false;
            }

            iconName = instance.m_location.m_prefab.Name;
            return true;
        }

        private static string BuildLocationKey(Vector2i zone, string iconName)
        {
            return $"{zone.x}:{zone.y}:{iconName}";
        }

        private static ZNetPeer? FindPeer(ZRpc rpc)
        {
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.m_rpc == rpc)
                {
                    return peer;
                }
            }

            return null;
        }

        private static PlayerIdentity GetPlayerIdentity(ZNetPeer peer)
        {
            long playerId = 0L;
            string playerName = "";

            if (!peer.m_characterID.IsNone() && ZDOMan.instance != null)
            {
                ZDO? playerZdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (playerZdo != null)
                {
                    playerId = playerZdo.GetLong(ZDOVars.s_playerID);
                    playerName = SanitizePlayerName(playerZdo.GetString(ZDOVars.s_playerName, ""));
                }
            }

            if (string.IsNullOrWhiteSpace(playerName))
            {
                playerName = SanitizePlayerName(peer.m_playerName ?? "");
            }

            return new PlayerIdentity(playerId, playerName);
        }

        private static HashSet<string> GetDiscoveries(string playerKey)
        {
            string sanitized = SanitizePlayerName(playerKey);
            if (!DiscoveriesByPlayer.TryGetValue(sanitized, out HashSet<string> discoveries))
            {
                discoveries = new HashSet<string>(StringComparer.Ordinal);
                DiscoveriesByPlayer[sanitized] = discoveries;
            }

            return discoveries;
        }

        private static void EnsureDiscoveryStateLoaded()
        {
            if (_discoveriesLoaded)
            {
                return;
            }

            _discoveriesLoaded = true;
            DiscoveriesByPlayer.Clear();
            string path = GetDiscoveryFilePath();

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
                string locationKey = parts[1].Trim();
                if (!string.IsNullOrWhiteSpace(playerName) && !string.IsNullOrWhiteSpace(locationKey))
                {
                    GetDiscoveries(playerName).Add(locationKey);
                }
            }
        }

        private static void SaveDiscoveryState()
        {
            string path = GetDiscoveryFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Paths.ConfigPath);

            List<string> lines = new();
            foreach (KeyValuePair<string, HashSet<string>> playerEntry in DiscoveriesByPlayer.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string locationKey in playerEntry.Value.OrderBy(key => key, StringComparer.Ordinal))
                {
                    lines.Add($"{EscapeTsv(playerEntry.Key)}\t{locationKey}");
                }
            }

            File.WriteAllLines(path, lines);
        }

        private static string GetDiscoveryFilePath()
        {
            string configured = ModConfig.LocationIconDiscoveryFile.Value.Trim();
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Paths.ConfigPath, configured);
        }

        private static float GetRevealDistance()
        {
            return Mathf.Max(0.0f, ModConfig.LocationIconRevealDistance.Value);
        }

        private static float DistanceXZ(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugPerPlayerLocationIcons.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
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

        private readonly struct PlayerIdentity
        {
            internal PlayerIdentity(long playerId, string playerName)
            {
                PlayerId = playerId;
                PlayerName = playerName;
            }

            internal long PlayerId { get; }
            internal string PlayerName { get; }
        }

        private readonly struct LocationIconCandidate
        {
            internal LocationIconCandidate(string key, Vector3 position, string iconName)
            {
                Key = key;
                Position = position;
                IconName = iconName;
            }

            internal string Key { get; }
            internal Vector3 Position { get; }
            internal string IconName { get; }
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "SendLocationIcons")]
    internal static class ZoneSystemSendLocationIconsPatch
    {
        private static bool Prefix(ZoneSystem __instance, long peer)
        {
            return !PerPlayerLocationIcons.TrySendFilteredLocationIcons(__instance, peer);
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_ServerSyncedPlayerData")]
    internal static class ZNetServerSyncedPlayerDataPatch
    {
        private static void Postfix(ZRpc rpc)
        {
            PerPlayerLocationIcons.OnServerSyncedPlayerData(rpc);
        }
    }
}
