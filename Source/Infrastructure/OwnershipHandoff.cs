using System;
using UnityEngine;

namespace ServerSideTweaks.Infrastructure
{
    internal static class OwnershipHandoff
    {
        internal static bool TryRouteToSender<T>(
            ZRoutedRpc.RoutedRPCData rpcData,
            string targetName,
            Action<string> debugLog) where T : Component
        {
            if (rpcData.m_targetZDO.IsNone())
            {
                return false;
            }

            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null || ZNet.instance == null)
            {
                return false;
            }

            ZDO? target = zdoMan.GetZDO(rpcData.m_targetZDO);
            if (target == null || !ZdoComponentLookup.HasComponent<T>(target))
            {
                return false;
            }

            if (ZNet.instance.GetPeer(rpcData.m_senderPeerID) == null)
            {
                return false;
            }

            if (target.GetOwner() != rpcData.m_senderPeerID)
            {
                TemporaryOwnershipHandoffs.Assign(target, rpcData.m_senderPeerID, targetName);
                zdoMan.ForceSendZDO(rpcData.m_senderPeerID, rpcData.m_targetZDO);
                debugLog($"Transferred {targetName} ownership for {rpcData.m_targetZDO} to {rpcData.m_senderPeerID}.");
            }
            else
            {
                TemporaryOwnershipHandoffs.RefreshIfTracked(rpcData.m_targetZDO, rpcData.m_senderPeerID);
            }

            rpcData.m_targetPeerID = rpcData.m_senderPeerID;
            return true;
        }
    }
}
