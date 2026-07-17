using HarmonyLib;
using ServerSideTweaks.Features.BossStones;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
    internal static class ZRoutedRpcRouteRpcPatch
    {
        [HarmonyPriority(Priority.First)]
        [HarmonyBefore("redseiko.valheim.enroute")]
        private static bool Prefix(ZRoutedRpc.RoutedRPCData rpcData)
        {
            BossLocationDiscoveryDiagnostics.LogRouteRpc(rpcData);
            return RoutedRpcDispatcher.Process(rpcData);
        }
    }
}
