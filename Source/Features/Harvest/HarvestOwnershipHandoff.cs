using System;
using ServerSideTweaks.Infrastructure;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks.Features.Harvest
{
    internal static class HarvestOwnershipHandoff
    {
        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("RPC_Extract", HandleExtract);
        }

        private static RoutedRpcAction HandleExtract(ZRoutedRpc.RoutedRPCData rpcData)
        {
            TryApply(rpcData);
            return RoutedRpcAction.Continue;
        }

        private static void TryApply(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ModConfig.EnableHarvestOwnershipHandoff.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            try
            {
                if (OwnershipHandoff.TryRouteToSender<Beehive>(rpcData, "Beehive", DebugLog))
                {
                    return;
                }

                OwnershipHandoff.TryRouteToSender<SapCollector>(rpcData, "SapCollector", DebugLog);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to apply harvest ownership handoff: {ex}");
            }
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugHarvestOwnershipHandoff.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }
    }
}
