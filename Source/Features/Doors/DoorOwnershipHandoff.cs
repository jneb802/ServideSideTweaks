using System;
using UnityEngine;
using ServideSideTweaks.Infrastructure;

namespace ServideSideTweaks.Features.Doors
{
    internal static class DoorOwnershipHandoff
    {
        private static readonly int UseDoorHash = "UseDoor".GetStableHashCode();

        internal static void TryApply(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ModConfig.EnableDoorOwnershipHandoff.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            if (rpcData.m_methodHash != UseDoorHash || rpcData.m_targetZDO.IsNone())
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
                    zdoMan.ForceSendZDO(rpcData.m_senderPeerID, rpcData.m_targetZDO);
                    target.SetOwner(rpcData.m_senderPeerID);
                    DebugLog($"Transferred door ownership for {rpcData.m_targetZDO} to {rpcData.m_senderPeerID}.");
                }

                rpcData.m_targetPeerID = rpcData.m_senderPeerID;
            }
            catch (Exception ex)
            {
                ServideSideTweaksPlugin.ModLogger.LogWarning($"Failed to apply door ownership handoff: {ex}");
            }
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugDoorOwnershipHandoff.Value)
            {
                ServideSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }
    }
}
