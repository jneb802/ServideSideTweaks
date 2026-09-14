using System;
using HarmonyLib;
using Splatform;

namespace ServerSideTweaks.Features.Map
{
    // Server-side adaptation of VentureValheim MultiplayerTweaks MapTweaks:
    // https://github.com/OrianaVenture/VentureValheim/blob/master/MultiplayerTweaks/src/MapTweaks.cs
    internal static class ForcePublicPlayerPositions
    {
        internal static void Apply(ZNet znet, ZRpc rpc)
        {
            if (ModConfig.ForcePublicPlayerPositions.Value != true || !znet.IsServer())
            {
                return;
            }

            ZNetPeer? peer = znet.GetPeer(rpc);
            if (peer == null || peer.m_publicRefPos || IsExemptAdmin(znet, rpc))
            {
                return;
            }

            peer.m_publicRefPos = true;
            if (ModConfig.DebugForcePublicPlayerPositions.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo($"Forced public map position for {FormatPeer(peer)}.");
            }
        }

        private static bool IsExemptAdmin(ZNet znet, ZRpc rpc)
        {
            string networkId = rpc.GetSocket().GetHostName();
            if (string.IsNullOrWhiteSpace(networkId) || !znet.IsAdmin(networkId))
            {
                return false;
            }

            string[] exemptAdminIds = ModConfig.ForcePublicPlayerPositionExemptAdminIds.Value.Split(
                new[] { ',', ';', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string exemptAdminId in exemptAdminIds)
            {
                if (NetworkIdsMatch(exemptAdminId.Trim(), networkId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool NetworkIdsMatch(string configuredId, string connectedId)
        {
            if (string.Equals(configuredId, connectedId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!TryParseNetworkId(connectedId, out PlatformUserID connectedUserId))
            {
                return false;
            }

            PlatformUserID displayUserId = PlatformUserID.FilterPlatformUserID(connectedUserId);
            if (string.Equals(configuredId, connectedUserId.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(configuredId, displayUserId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (TryParseNetworkId(configuredId, out PlatformUserID configuredUserId) &&
                configuredUserId == connectedUserId)
            {
                return true;
            }

            return connectedUserId.m_platform == "Steam" &&
                   string.Equals(configuredId, connectedUserId.m_userID, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseNetworkId(string networkId, out PlatformUserID platformUserId)
        {
            if (PlatformUserID.TryParse(networkId, out platformUserId))
            {
                return true;
            }

            if (ulong.TryParse(networkId, out ulong steamId))
            {
                platformUserId = new PlatformUserID("Steam", steamId);
                return platformUserId.IsValid;
            }

            platformUserId = PlatformUserID.None;
            return false;
        }

        private static string FormatPeer(ZNetPeer peer)
        {
            string playerName = string.IsNullOrWhiteSpace(peer.m_playerName) ? "<unknown>" : peer.m_playerName;
            return $"{playerName} ({peer.m_uid})";
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_ServerSyncedPlayerData")]
    internal static class ZNetForcePublicPlayerPositionsPatch
    {
        private static void Postfix(ZNet __instance, ZRpc rpc)
        {
            ForcePublicPlayerPositions.Apply(__instance, rpc);
        }
    }
}
