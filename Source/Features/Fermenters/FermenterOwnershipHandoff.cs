using System;
using ServerSideTweaks.Infrastructure;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks.Features.Fermenters
{
    internal static class FermenterOwnershipHandoff
    {
        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("RPC_AddItem", HandleFermenterRpc);
            RoutedRpcDispatcher.Register("RPC_Tap", HandleFermenterRpc);
        }

        private static RoutedRpcAction HandleFermenterRpc(ZRoutedRpc.RoutedRPCData rpcData)
        {
            TryApply(rpcData);
            return RoutedRpcAction.Continue;
        }

        private static void TryApply(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ModConfig.EnableFermenterOwnershipHandoff.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            try
            {
                OwnershipHandoff.TryRouteToSender<Fermenter>(rpcData, "Fermenter", DebugLog);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to apply fermenter ownership handoff: {ex}");
            }
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugFermenterOwnershipHandoff.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }
    }
}
