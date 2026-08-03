using System;
using UnityEngine;
using ServerSideTweaks.Infrastructure;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks.Features.Doors
{
    internal static class DoorOwnershipHandoff
    {
        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("UseDoor", HandleUseDoor);
        }

        private static RoutedRpcAction HandleUseDoor(ZRoutedRpc.RoutedRPCData rpcData)
        {
            TryApply(rpcData);
            return RoutedRpcAction.Continue;
        }

        private static void TryApply(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ModConfig.EnableDoorOwnershipHandoff.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            if (rpcData.m_targetZDO.IsNone())
            {
                return;
            }

            try
            {
                ZDOMan zdoMan = ZDOMan.instance;
                if (zdoMan == null)
                {
                    return;
                }

                ZDO? target = zdoMan.GetZDO(rpcData.m_targetZDO);
                if (target == null || !ZdoComponentLookup.HasComponent<Door>(target))
                {
                    return;
                }

                if (ZNet.instance.GetPeer(rpcData.m_senderPeerID) == null)
                {
                    return;
                }

                if (target.GetOwner() != rpcData.m_senderPeerID)
                {
                    TemporaryOwnershipHandoffs.Assign(target, rpcData.m_senderPeerID);
                    zdoMan.ForceSendZDO(rpcData.m_senderPeerID, rpcData.m_targetZDO);
                    DebugLog($"Transferred door ownership for {rpcData.m_targetZDO} to {rpcData.m_senderPeerID}.");
                }
                else
                {
                    TemporaryOwnershipHandoffs.RefreshIfTracked(rpcData.m_targetZDO, rpcData.m_senderPeerID);
                }

                rpcData.m_targetPeerID = rpcData.m_senderPeerID;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to apply door ownership handoff: {ex}");
            }
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugDoorOwnershipHandoff.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }
    }
}
