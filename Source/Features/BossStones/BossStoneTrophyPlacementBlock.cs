using System;
using ServerSideTweaks.Infrastructure.Routing;
using UnityEngine;

namespace ServerSideTweaks.Features.BossStones
{
    internal static class BossStoneTrophyPlacementBlock
    {
        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("RPC_RequestOwn", HandleRequestOwn);
        }

        private static RoutedRpcAction HandleRequestOwn(ZRoutedRpc.RoutedRPCData rpcData)
        {
            return ShouldBlockRequestOwn(rpcData) ? RoutedRpcAction.Consume : RoutedRpcAction.Continue;
        }

        private static bool ShouldBlockRequestOwn(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ModConfig.EnableBossStoneTrophyPlacementBlock.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return false;
            }

            if (rpcData.m_targetZDO.IsNone())
            {
                return false;
            }

            try
            {
                ZDOMan zdoMan = ZDOMan.instance;
                if (zdoMan == null)
                {
                    return false;
                }

                ZDO? target = zdoMan.GetZDO(rpcData.m_targetZDO);
                if (target == null || !IsBossStoneItemStand(target))
                {
                    return false;
                }

                zdoMan.ForceSendZDO(rpcData.m_senderPeerID, rpcData.m_targetZDO);
                DebugLog($"Blocked boss-stone item stand ownership request for {rpcData.m_targetZDO} from {rpcData.m_senderPeerID}.");
                return true;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to block boss-stone trophy placement: {ex}");
                return false;
            }
        }

        private static bool IsBossStoneItemStand(ZDO zdo)
        {
            ZNetView? instance = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (instance != null)
            {
                ItemStand? itemStand = instance.GetComponent<ItemStand>();
                return itemStand != null && IsBossStoneStand(itemStand);
            }

            GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            ItemStand? prefabStand = prefab != null ? prefab.GetComponent<ItemStand>() : null;
            return prefabStand != null && IsBossStoneStand(prefabStand);
        }

        private static bool IsBossStoneStand(ItemStand itemStand)
        {
            return itemStand.GetComponentInParent<BossStone>() != null ||
                itemStand.m_guardianPower != null && !itemStand.m_canBeRemoved;
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugBossStoneTrophyPlacementBlock.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }
    }
}
