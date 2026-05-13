using System;
using ServerSideTweaks.Infrastructure;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks.Features.Mining
{
    internal static class MineRockOwnershipHandoff
    {
        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("Hit", HandleMineRockHit);
            RoutedRpcDispatcher.Register("RPC_Damage", HandleMineRock5Damage);
        }

        private static RoutedRpcAction HandleMineRockHit(ZRoutedRpc.RoutedRPCData rpcData)
        {
            TryApply(rpcData, TargetKind.MineRock);
            return RoutedRpcAction.Continue;
        }

        private static RoutedRpcAction HandleMineRock5Damage(ZRoutedRpc.RoutedRPCData rpcData)
        {
            TryApply(rpcData, TargetKind.MineRock5);
            return RoutedRpcAction.Continue;
        }

        private static void TryApply(ZRoutedRpc.RoutedRPCData rpcData, TargetKind expectedKind)
        {
            if (ModConfig.EnableMineRockOwnershipHandoff.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
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
                if (target == null || GetTargetKind(target) != expectedKind)
                {
                    return;
                }

                HitData hit = ReadHitData(rpcData.m_parameters);
                if (!IsPlayerPickaxeHit(hit, rpcData.m_senderPeerID))
                {
                    DebugLog($"Ignored {expectedKind} ownership handoff for {rpcData.m_targetZDO}: hit was not verified player pickaxe damage.");
                    return;
                }

                ZNetPeer peer = ZNet.instance.GetPeer(rpcData.m_senderPeerID);
                if (peer == null || peer.m_characterID != hit.m_attacker)
                {
                    DebugLog($"Ignored {expectedKind} ownership handoff for {rpcData.m_targetZDO}: sender did not match attacker.");
                    return;
                }

                if (target.GetOwner() != rpcData.m_senderPeerID)
                {
                    target.SetOwner(rpcData.m_senderPeerID);
                    zdoMan.ForceSendZDO(rpcData.m_senderPeerID, rpcData.m_targetZDO);
                    DebugLog($"Transferred {expectedKind} ownership for {rpcData.m_targetZDO} to {rpcData.m_senderPeerID}.");
                }

                rpcData.m_targetPeerID = rpcData.m_senderPeerID;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to apply MineRock ownership handoff: {ex}");
            }
        }

        private static HitData ReadHitData(ZPackage parameters)
        {
            parameters.SetPos(0);
            HitData hit = new();
            hit.Deserialize(ref parameters);
            parameters.SetPos(0);
            return hit;
        }

        private static bool IsPlayerPickaxeHit(HitData hit, long sender)
        {
            return hit.m_hitType == HitData.HitType.PlayerHit
                && !hit.m_attacker.IsNone()
                && hit.m_attacker.UserID == sender
                && hit.m_damage.m_pickaxe > 0.0f;
        }

        private static TargetKind GetTargetKind(ZDO zdo)
        {
            if (ZdoComponentLookup.HasComponent<MineRock>(zdo))
            {
                return TargetKind.MineRock;
            }

            return ZdoComponentLookup.HasComponent<MineRock5>(zdo) ? TargetKind.MineRock5 : TargetKind.None;
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugMineRockOwnershipHandoff.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private enum TargetKind
        {
            None,
            MineRock,
            MineRock5
        }
    }
}
