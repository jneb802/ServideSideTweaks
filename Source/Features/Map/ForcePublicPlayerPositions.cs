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

            ZNetPeer peer = znet.GetPeer(rpc);
            if (peer == null || peer.m_publicRefPos)
            {
                return;
            }

            peer.m_publicRefPos = true;
            if (ModConfig.DebugForcePublicPlayerPositions.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo($"Forced public map position for {FormatPeer(peer)}.");
            }
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
