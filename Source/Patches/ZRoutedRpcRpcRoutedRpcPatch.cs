using HarmonyLib;
using ServerSideTweaks.Features.BossStones;
using ServerSideTweaks.Features.Bosses;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    internal static class ZRoutedRpcRpcRoutedRpcPatch
    {
        [HarmonyPriority(Priority.First)]
        [HarmonyBefore("redseiko.valheim.enroute")]
        private static bool Prefix(ZRoutedRpc __instance, ZPackage pkg)
        {
            if (!__instance.m_server)
            {
                return true;
            }

            try
            {
                pkg.SetPos(0);
                ZRoutedRpc.RoutedRPCData rpcData = new();
                rpcData.Deserialize(pkg);
                pkg.SetPos(0);

                BossLocationDiscoveryDiagnostics.LogIncomingRoutedRpc(rpcData);
                if (BossLocationDiscoveryDiagnostics.TryHandleServerDiscoveryRequest(__instance, rpcData))
                {
                    return false;
                }

                BossStoneTrophyPlacementBlock.NotifyBlockedInteraction(rpcData);
                if (BossStoneTrophyPlacementBlock.TryConsumeVisualItem(rpcData))
                {
                    return false;
                }
                if (BossMessage.TryConsumeIncomingRoutedRpc(rpcData))
                {
                    return false;
                }

                if (rpcData.m_targetPeerID != __instance.m_id || rpcData.m_targetZDO.IsNone())
                {
                    return true;
                }

                // Server-addressed object calls never enter RouteRPC. Apply the
                // same handlers here, before vanilla attempts local delivery.
                if (!RoutedRpcDispatcher.Process(rpcData))
                {
                    return false;
                }

                if (rpcData.m_targetPeerID == __instance.m_id)
                {
                    return true;
                }

                // Forward the rewritten call once, preserving its original sender.
                // Calling RouteRPC here would run the ownership handlers twice.
                ZNetPeer targetPeer = __instance.GetPeer(rpcData.m_targetPeerID);
                if (targetPeer != null && targetPeer.IsReady())
                {
                    ZPackage forwarded = new();
                    rpcData.Serialize(forwarded);
                    targetPeer.m_rpc.Invoke("RoutedRPC", forwarded);
                }
                return false;
            }
            catch (System.Exception ex)
            {
                pkg.SetPos(0);
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to inspect incoming routed RPC: {ex}");
                return true;
            }
        }
    }
}
