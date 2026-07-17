using System;
using System.Collections.Generic;
using UnityEngine;

namespace ServerSideTweaks.Features.BossStones
{
    internal static class BossStoneTrophyPlacementBlock
    {
        private const string PlacementBlockedMessage = "You must build a boss stone to place trophy";
        private const string StartTempleLocationName = "StartTemple";
        private static readonly int RequestOwnHash = "RPC_RequestOwn".GetStableHashCode();
        private static readonly int SetVisualItemHash = "SetVisualItem".GetStableHashCode();

        private static readonly int[] BossStonePrefabHashes =
        {
            "BossStone_Eikthyr".GetStableHashCode(),
            "BossStone_TheElder".GetStableHashCode(),
            "BossStone_Bonemass".GetStableHashCode(),
            "BossStone_DragonQueen".GetStableHashCode(),
            "BossStone_Yagluth".GetStableHashCode(),
            "BossStone_TheQueen".GetStableHashCode(),
            "BossStone_Fader".GetStableHashCode()
        };

        private static readonly HashSet<string> BossTrophyNames = new(StringComparer.Ordinal)
        {
            "TrophyEikthyr",
            "TrophyTheElder",
            "TrophyBonemass",
            "TrophyDragonQueen",
            "TrophyGoblinKing",
            "TrophySeekerQueen",
            "TrophyFader"
        };

        internal static bool AllowZdoStringSet(ZDOID zdoId, int hash, string value)
        {
            if (!ShouldBlockZdoItemSet(zdoId, hash, value))
            {
                return true;
            }

            try
            {
                ZDOMan zdoMan = ZDOMan.instance;
                if (zdoMan == null)
                {
                    return false;
                }

                ZDO? zdo = zdoMan.GetZDO(zdoId);
                ClearBossStoneAttachment(zdoMan, zdo, 0L);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to restore boss-stone ownership after blocking trophy placement: {ex}");
            }

            ServerSideTweaksPlugin.ModLogger.LogInfo($"Blocked boss-stone trophy ZDO item write. zdo={zdoId} item={value}");
            return false;
        }

        internal static bool TryBlockVisualItemRpc(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (rpcData.m_methodHash != SetVisualItemHash || !IsEnabledOnServer() || rpcData.m_targetZDO.IsNone())
            {
                return false;
            }

            ZDOMan? zdoMan = ZDOMan.instance;
            if (zdoMan == null)
            {
                return false;
            }

            ZDO? zdo = zdoMan.GetZDO(rpcData.m_targetZDO);
            if (zdo == null || !IsStartTempleBossStone(zdo))
            {
                return false;
            }

            string itemName = ReadSetVisualItemName(rpcData.m_parameters);
            if (string.IsNullOrEmpty(itemName) || !BossTrophyNames.Contains(itemName))
            {
                return false;
            }

            try
            {
                ClearBossStoneAttachment(zdoMan, zdo, rpcData.m_senderPeerID);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to clear boss-stone visual after blocking trophy placement: {ex}");
            }

            ZRoutedRpc.instance?.InvokeRoutedRPC(
                rpcData.m_senderPeerID,
                "ShowMessage",
                (int)MessageHud.MessageType.Center,
                PlacementBlockedMessage);

            ServerSideTweaksPlugin.ModLogger.LogInfo(
                $"Blocked boss-stone trophy visual RPC. zdo={rpcData.m_targetZDO} item={itemName} sender={rpcData.m_senderPeerID}");
            return true;
        }

        internal static bool TryBlockOwnershipRequest(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (rpcData.m_methodHash != RequestOwnHash || !IsEnabledOnServer() || rpcData.m_targetZDO.IsNone())
            {
                return false;
            }

            ZDOMan? zdoMan = ZDOMan.instance;
            if (zdoMan == null)
            {
                return false;
            }

            ZDO? zdo = zdoMan.GetZDO(rpcData.m_targetZDO);
            if (zdo == null || !IsStartTempleBossStone(zdo))
            {
                return false;
            }

            try
            {
                zdo.SetOwner(zdoMan.m_sessionID);
                zdoMan.ForceSendZDO(rpcData.m_targetZDO);
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to retain boss-stone ownership after blocking trophy placement: {ex}");
            }

            ZRoutedRpc.instance?.InvokeRoutedRPC(
                rpcData.m_senderPeerID,
                "ShowMessage",
                (int)MessageHud.MessageType.Center,
                PlacementBlockedMessage);

            ServerSideTweaksPlugin.ModLogger.LogInfo(
                $"Blocked boss-stone ownership request. zdo={rpcData.m_targetZDO} sender={rpcData.m_senderPeerID}");
            return true;
        }

        private static void ClearBossStoneAttachment(ZDOMan zdoMan, ZDO? zdo, long senderPeerId)
        {
            if (zdo == null)
            {
                return;
            }

            zdo.SetOwner(zdoMan.m_sessionID);
            zdo.Set(ZDOVars.s_item, "");
            zdo.Set(ZDOVars.s_variant, 0, false);
            zdo.Set(ZDOVars.s_quality, 1, false);
            zdo.Set(ZDOVars.s_type, 0, false);
            zdo.DataRevision = Math.Max(zdo.DataRevision + 1000U, 1000U);

            if (senderPeerId != 0L)
            {
                zdoMan.ForceSendZDO(senderPeerId, zdo.m_uid);
            }

            zdoMan.ForceSendZDO(zdo.m_uid);
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, zdo.m_uid, "SetVisualItem", "", 0, 1, 0);
        }

        private static string ReadSetVisualItemName(ZPackage parameters)
        {
            try
            {
                ZPackage copy = new(parameters.GetArray());
                return copy.ReadString();
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to read boss-stone SetVisualItem parameters: {ex}");
                return "";
            }
        }

        private static bool ShouldBlockZdoItemSet(ZDOID zdoId, int hash, string value)
        {
            if (!IsEnabledOnServer() || hash != ZDOVars.s_item || string.IsNullOrEmpty(value) || !BossTrophyNames.Contains(value))
            {
                return false;
            }

            ZDO? zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(zdoId) : null;
            return zdo != null && IsStartTempleBossStone(zdo);
        }

        private static bool IsStartTempleBossStone(ZDO zdo)
        {
            return IsBossStonePrefab(zdo.GetPrefab()) && IsInsideStartTemple(zdo.GetPosition());
        }

        internal static bool IsBossStoneZdo(ZDO zdo)
        {
            return IsBossStonePrefab(zdo.GetPrefab());
        }

        private static bool IsBossStonePrefab(int prefabHash)
        {
            for (int i = 0; i < BossStonePrefabHashes.Length; i++)
            {
                if (BossStonePrefabHashes[i] == prefabHash)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInsideStartTemple(Vector3 position)
        {
            if (ZoneSystem.instance == null)
            {
                return false;
            }

            foreach (ZoneSystem.LocationInstance location in ZoneSystem.instance.GetLocationList())
            {
                if (!IsStartTemple(location))
                {
                    continue;
                }

                float radius = Math.Max(location.m_location.m_exteriorRadius, 20f);
                if (Vector3.Distance(position, location.m_position) <= radius + 5f)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStartTemple(ZoneSystem.LocationInstance location)
        {
            ZoneSystem.ZoneLocation zoneLocation = location.m_location;
            if (zoneLocation == null)
            {
                return false;
            }

            if (string.Equals(zoneLocation.m_prefabName, StartTempleLocationName, StringComparison.Ordinal))
            {
                return true;
            }

            return zoneLocation.m_prefab != null &&
                string.Equals(zoneLocation.m_prefab.Name, StartTempleLocationName, StringComparison.Ordinal);
        }

        private static bool IsEnabledOnServer()
        {
            return ModConfig.EnableBossStoneTrophyPlacementBlock.Value &&
                ZNet.instance != null &&
                ZNet.instance.IsServer() &&
                ZDOMan.instance != null;
        }
    }
}
