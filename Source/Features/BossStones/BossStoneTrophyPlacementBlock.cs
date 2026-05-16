using System;
using ServerSideTweaks.Infrastructure.Routing;
using UnityEngine;

namespace ServerSideTweaks.Features.BossStones
{
    internal static class BossStoneTrophyPlacementBlock
    {
        private static readonly int RequestOwnHash = "RPC_RequestOwn".GetStableHashCode();
        private static readonly int SetVisualItemHash = "SetVisualItem".GetStableHashCode();
        private static readonly int UpdateVisualHash = "RPC_UpdateVisual".GetStableHashCode();
        private static readonly int DestroyAttachmentHash = "RPC_DestroyAttachment".GetStableHashCode();
        private static readonly int DropItemHash = "RPC_DropItem".GetStableHashCode();

        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("RPC_RequestOwn", HandleRequestOwn);
            RoutedRpcDispatcher.Register("SetVisualItem", HandleObservedRpc);
            RoutedRpcDispatcher.Register("RPC_UpdateVisual", HandleObservedRpc);
            RoutedRpcDispatcher.Register("RPC_DestroyAttachment", HandleObservedRpc);
            RoutedRpcDispatcher.Register("RPC_DropItem", HandleObservedRpc);
        }

        internal static bool ProcessIncomingRoutedRpc(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!IsWatchedMethod(rpcData.m_methodHash))
            {
                return true;
            }

            if (rpcData.m_methodHash == RequestOwnHash && ShouldBlockRequestOwn(rpcData, "local"))
            {
                return false;
            }

            if (rpcData.m_methodHash == SetVisualItemHash && ShouldBlockSetVisualItem(rpcData, "local"))
            {
                return false;
            }

            LogObservedRpc(rpcData, "local");
            return true;
        }

        private static RoutedRpcAction HandleRequestOwn(ZRoutedRpc.RoutedRPCData rpcData)
        {
            return ShouldBlockRequestOwn(rpcData, "route") ? RoutedRpcAction.Consume : RoutedRpcAction.Continue;
        }

        private static RoutedRpcAction HandleObservedRpc(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (rpcData.m_methodHash == SetVisualItemHash && ShouldBlockSetVisualItem(rpcData, "route"))
            {
                return RoutedRpcAction.Consume;
            }

            LogObservedRpc(rpcData, "route");
            return RoutedRpcAction.Continue;
        }

        private static bool ShouldBlockRequestOwn(ZRoutedRpc.RoutedRPCData rpcData, string path)
        {
            if (ModConfig.EnableBossStoneTrophyPlacementBlock.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return false;
            }

            if (rpcData.m_targetZDO.IsNone())
            {
                DebugLog($"BossStoneTrophyPlacement {path}: RPC_RequestOwn has no target ZDO. sender={DescribePeer(rpcData.m_senderPeerID)} targetPeer={rpcData.m_targetPeerID}");
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
                if (target == null)
                {
                    DebugLog($"BossStoneTrophyPlacement {path}: RPC_RequestOwn target ZDO not found. zdo={rpcData.m_targetZDO} sender={DescribePeer(rpcData.m_senderPeerID)} targetPeer={rpcData.m_targetPeerID}");
                    return false;
                }

                TargetInfo info = DescribeTarget(target);
                DebugLog($"BossStoneTrophyPlacement {path}: RPC_RequestOwn observed. {FormatRpc(rpcData, info)}");
                if (!info.IsBossStoneStand)
                {
                    return false;
                }

                zdoMan.ForceSendZDO(rpcData.m_senderPeerID, rpcData.m_targetZDO);
                DebugLog($"BossStoneTrophyPlacement {path}: blocked boss-stone item stand ownership request. {FormatRpc(rpcData, info)}");
                return true;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to block boss-stone trophy placement: {ex}");
                return false;
            }
        }

        private static bool ShouldBlockSetVisualItem(ZRoutedRpc.RoutedRPCData rpcData, string path)
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
                if (target == null)
                {
                    return false;
                }

                TargetInfo info = DescribeTarget(target);
                SetVisualItemParameters parameters = ReadSetVisualItemParameterValues(rpcData.m_parameters);
                if (!info.IsBossStoneStand || string.IsNullOrEmpty(parameters.ItemName))
                {
                    return false;
                }

                ClearBossStoneAttachment(zdoMan, target, rpcData.m_senderPeerID);
                DebugLog($"BossStoneTrophyPlacement {path}: blocked boss-stone SetVisualItem. params={parameters} {FormatRpc(rpcData, info)}");
                return true;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to block boss-stone visual item placement: {ex}");
                return false;
            }
        }

        private static void ClearBossStoneAttachment(ZDOMan zdoMan, ZDO target, long senderPeerId)
        {
            target.Set(ZDOVars.s_item, "");
            target.Set(ZDOVars.s_variant, 0, false);
            target.Set(ZDOVars.s_quality, 1, false);
            target.Set(ZDOVars.s_type, 0, false);
            target.SetOwner(0L);
            target.DataRevision = Math.Max(target.DataRevision + 1000U, 1000U);

            zdoMan.ForceSendZDO(senderPeerId, target.m_uid);
            zdoMan.ForceSendZDO(target.m_uid);
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, target.m_uid, "SetVisualItem", "", 0, 0, 0);
            }
        }

        private static bool IsBossStoneItemStand(ZDO zdo)
        {
            return DescribeTarget(zdo).IsBossStoneStand;
        }

        private static void LogObservedRpc(ZRoutedRpc.RoutedRPCData rpcData, string path)
        {
            if (!ShouldDebugLog() || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            if (rpcData.m_targetZDO.IsNone())
            {
                DebugLog($"BossStoneTrophyPlacement {path}: {GetMethodName(rpcData.m_methodHash)} has no target ZDO. sender={DescribePeer(rpcData.m_senderPeerID)} targetPeer={rpcData.m_targetPeerID}");
                return;
            }

            try
            {
                ZDO? target = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(rpcData.m_targetZDO) : null;
                if (target == null)
                {
                    DebugLog($"BossStoneTrophyPlacement {path}: {GetMethodName(rpcData.m_methodHash)} target ZDO not found. zdo={rpcData.m_targetZDO} sender={DescribePeer(rpcData.m_senderPeerID)} targetPeer={rpcData.m_targetPeerID}");
                    return;
                }

                TargetInfo info = DescribeTarget(target);
                string setVisualItem = rpcData.m_methodHash == SetVisualItemHash ? $" params={ReadSetVisualItemParameterValues(rpcData.m_parameters)}" : "";
                if (info.HasItemStand || info.IsBossStoneStand || rpcData.m_methodHash == RequestOwnHash || setVisualItem.IndexOf("Trophy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    DebugLog($"BossStoneTrophyPlacement {path}: {GetMethodName(rpcData.m_methodHash)} observed.{setVisualItem} {FormatRpc(rpcData, info)}");
                }
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to log boss-stone trophy placement RPC: {ex}");
            }
        }

        private static TargetInfo DescribeTarget(ZDO zdo)
        {
            ZNetView? instance = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            GameObject? instanceObject = instance != null ? instance.gameObject : null;
            ItemStand? itemStand = FindItemStand(instanceObject);
            BossStone? bossStone = FindBossStone(instanceObject);

            GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            ItemStand? prefabStand = FindItemStand(prefab);
            BossStone? prefabBossStone = FindBossStone(prefab);
            ItemStand? bestStand = itemStand != null ? itemStand : prefabStand;

            return new TargetInfo(
                zdo.GetPrefab(),
                prefab != null ? prefab.name : "<unknown>",
                zdo.GetPosition(),
                zdo.GetOwner(),
                zdo.GetString(ZDOVars.s_item),
                zdo.GetInt(ZDOVars.s_variant),
                zdo.GetInt(ZDOVars.s_quality, 1),
                instanceObject != null ? instanceObject.name : "<none>",
                instance != null,
                itemStand != null || prefabStand != null,
                bossStone != null || prefabBossStone != null || bestStand != null && bestStand.GetComponentInParent<BossStone>() != null,
                bestStand != null && bestStand.m_guardianPower != null,
                bestStand != null ? bestStand.m_guardianPower != null ? bestStand.m_guardianPower.name : "<none>" : "<no item stand>",
                bestStand != null && bestStand.m_canBeRemoved,
                bestStand != null ? bestStand.m_name : "<no item stand>");
        }

        private static ItemStand? FindItemStand(GameObject? gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            return gameObject.GetComponent<ItemStand>() ??
                gameObject.GetComponentInChildren<ItemStand>() ??
                gameObject.GetComponentInParent<ItemStand>();
        }

        private static BossStone? FindBossStone(GameObject? gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            return gameObject.GetComponent<BossStone>() ??
                gameObject.GetComponentInChildren<BossStone>() ??
                gameObject.GetComponentInParent<BossStone>();
        }

        private static bool IsBossStoneStand(ItemStand itemStand)
        {
            return itemStand.GetComponentInParent<BossStone>() != null ||
                itemStand.m_guardianPower != null && !itemStand.m_canBeRemoved;
        }

        private static bool IsWatchedMethod(int methodHash)
        {
            return methodHash == RequestOwnHash ||
                methodHash == SetVisualItemHash ||
                methodHash == UpdateVisualHash ||
                methodHash == DestroyAttachmentHash ||
                methodHash == DropItemHash;
        }

        private static string GetMethodName(int methodHash)
        {
            if (methodHash == RequestOwnHash) return "RPC_RequestOwn";
            if (methodHash == SetVisualItemHash) return "SetVisualItem";
            if (methodHash == UpdateVisualHash) return "RPC_UpdateVisual";
            if (methodHash == DestroyAttachmentHash) return "RPC_DestroyAttachment";
            if (methodHash == DropItemHash) return "RPC_DropItem";
            return methodHash.ToString();
        }

        private static SetVisualItemParameters ReadSetVisualItemParameterValues(ZPackage parameters)
        {
            int pos = parameters.GetPos();
            try
            {
                parameters.SetPos(0);
                string itemName = parameters.ReadString();
                int variant = parameters.ReadInt();
                int quality = parameters.ReadInt();
                int orientation = parameters.ReadInt();
                return new SetVisualItemParameters(itemName, variant, quality, orientation);
            }
            catch (Exception ex)
            {
                return new SetVisualItemParameters($"<failed to read: {ex.GetType().Name}>", 0, 0, 0);
            }
            finally
            {
                parameters.SetPos(pos);
            }
        }

        private static string FormatRpc(ZRoutedRpc.RoutedRPCData rpcData, TargetInfo info)
        {
            return $"zdo={rpcData.m_targetZDO} sender={DescribePeer(rpcData.m_senderPeerID)} targetPeer={rpcData.m_targetPeerID} owner={info.Owner} prefab={info.PrefabName} prefabHash={info.PrefabHash} pos={FormatVector(info.Position)} instance={info.InstanceName} instanceLoaded={info.InstanceLoaded} hasItemStand={info.HasItemStand} hasBossStone={info.HasBossStone} guardianPower={info.GuardianPowerName} hasGuardianPower={info.HasGuardianPower} canBeRemoved={info.CanBeRemoved} standName={info.StandName} zdoItem={info.ZdoItem} zdoVariant={info.ZdoVariant} zdoQuality={info.ZdoQuality}";
        }

        private static string DescribePeer(long peerId)
        {
            ZNetPeer? peer = ZNet.instance != null ? ZNet.instance.GetPeer(peerId) : null;
            return peer == null ? peerId.ToString() : $"{peerId}/{peer.m_playerName}/{peer.m_socket.GetHostName()}";
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"{vector.x:0.0},{vector.y:0.0},{vector.z:0.0}";
        }

        private static void DebugLog(string message)
        {
            if (ShouldDebugLog())
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private static bool ShouldDebugLog()
        {
            return ModConfig.DebugBossStoneTrophyPlacementBlock.Value;
        }

        private sealed class TargetInfo
        {
            internal TargetInfo(
                int prefabHash,
                string prefabName,
                Vector3 position,
                long owner,
                string zdoItem,
                int zdoVariant,
                int zdoQuality,
                string instanceName,
                bool instanceLoaded,
                bool hasItemStand,
                bool hasBossStone,
                bool hasGuardianPower,
                string guardianPowerName,
                bool canBeRemoved,
                string standName)
            {
                PrefabHash = prefabHash;
                PrefabName = prefabName;
                Position = position;
                Owner = owner;
                ZdoItem = zdoItem;
                ZdoVariant = zdoVariant;
                ZdoQuality = zdoQuality;
                InstanceName = instanceName;
                InstanceLoaded = instanceLoaded;
                HasItemStand = hasItemStand;
                HasBossStone = hasBossStone;
                HasGuardianPower = hasGuardianPower;
                GuardianPowerName = guardianPowerName;
                CanBeRemoved = canBeRemoved;
                StandName = standName;
            }

            internal int PrefabHash { get; }
            internal string PrefabName { get; }
            internal Vector3 Position { get; }
            internal long Owner { get; }
            internal string ZdoItem { get; }
            internal int ZdoVariant { get; }
            internal int ZdoQuality { get; }
            internal string InstanceName { get; }
            internal bool InstanceLoaded { get; }
            internal bool HasItemStand { get; }
            internal bool HasBossStone { get; }
            internal bool HasGuardianPower { get; }
            internal string GuardianPowerName { get; }
            internal bool CanBeRemoved { get; }
            internal string StandName { get; }
            internal bool IsBossStoneStand => HasBossStone || HasGuardianPower && !CanBeRemoved;
        }

        private sealed class SetVisualItemParameters
        {
            internal SetVisualItemParameters(string itemName, int variant, int quality, int orientation)
            {
                ItemName = itemName;
                Variant = variant;
                Quality = quality;
                Orientation = orientation;
            }

            internal string ItemName { get; }
            private int Variant { get; }
            private int Quality { get; }
            private int Orientation { get; }

            public override string ToString()
            {
                return $"item={ItemName} variant={Variant} quality={Quality} orientation={Orientation}";
            }
        }
    }
}
