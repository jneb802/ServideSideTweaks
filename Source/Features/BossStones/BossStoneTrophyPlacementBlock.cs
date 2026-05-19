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
                if (zdo != null)
                {
                    zdo.SetOwner(zdoMan.m_sessionID);
                    zdo.Set(ZDOVars.s_item, "");
                    zdo.Set(ZDOVars.s_type, 0, false);
                    zdoMan.ForceSendZDO(zdoId);
                }

                if (ZRoutedRpc.instance != null)
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, zdoId, "SetVisualItem", "", 0, 1, 0);
                }
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to restore boss-stone ownership after blocking trophy placement: {ex}");
            }

            ServerSideTweaksPlugin.ModLogger.LogInfo($"Blocked boss-stone trophy ZDO item write. zdo={zdoId} item={value}");
            return false;
        }

        internal static void NotifyBlockedInteraction(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (rpcData.m_methodHash != RequestOwnHash || !IsEnabledOnServer() || rpcData.m_targetZDO.IsNone())
            {
                return;
            }

            ZDO? zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(rpcData.m_targetZDO) : null;
            if (zdo == null || !IsStartTempleBossStone(zdo))
            {
                return;
            }

            ZRoutedRpc.instance?.InvokeRoutedRPC(
                rpcData.m_senderPeerID,
                "ShowMessage",
                (int)MessageHud.MessageType.Center,
                PlacementBlockedMessage);
        }

        internal static void NormalizeOwner(ZDO zdo, ref long owner)
        {
            if (!IsEnabledOnServer() || owner == ZDOMan.instance.m_sessionID || !IsStartTempleBossStone(zdo))
            {
                return;
            }

            ServerSideTweaksPlugin.ModLogger.LogInfo($"Kept start-temple boss stone server-owned. zdo={zdo.m_uid} requestedOwner={owner} pos={FormatVector(zdo.GetPosition())}");
            owner = ZDOMan.instance.m_sessionID;
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

        private static string FormatVector(Vector3 vector)
        {
            return $"{vector.x:0.0},{vector.y:0.0},{vector.z:0.0}";
        }
    }
}
