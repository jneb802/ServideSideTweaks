using System;
using HarmonyLib;
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
            try
            {
                return RoutedRpcDispatcher.Process(rpcData);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Routed RPC dispatcher failed before handlers ran; allowing vanilla RouteRPC to continue: {ex}");
                return true;
            }
        }
    }
}
