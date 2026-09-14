using System;
using HarmonyLib;

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
                if (string.Equals(exemptAdminId.Trim(), networkId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

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
