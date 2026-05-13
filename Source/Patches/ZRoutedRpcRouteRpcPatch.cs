using HarmonyLib;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
    internal static class ZRoutedRpcRouteRpcPatch
    {
        private static bool Prefix(ZRoutedRpc.RoutedRPCData rpcData)
        {
            return RoutedRpcDispatcher.Process(rpcData);
        }
    }
}
