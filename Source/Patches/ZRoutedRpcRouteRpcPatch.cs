using HarmonyLib;
using ServideSideTweaks.Infrastructure.Routing;

namespace ServideSideTweaks.Patches
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
