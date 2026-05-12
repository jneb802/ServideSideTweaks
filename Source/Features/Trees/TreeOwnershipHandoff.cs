using System;
using System.Collections.Generic;
using UnityEngine;

namespace ServideSideTweaks.Features.Trees
{
    internal static class TreeOwnershipHandoff
    {
        private static readonly int DamageRpcHash = "RPC_Damage".GetStableHashCode();
        private static readonly Dictionary<ZDOID, PendingHandoff> PendingHandoffs = new();
        private static readonly Dictionary<ZDOID, float> LastHandoffTimes = new();

        internal static void TrySchedule(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            if (rpcData.m_methodHash != DamageRpcHash || rpcData.m_targetZDO.IsNone())
            {
                return;
            }

            try
            {
                ZDO? target = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(rpcData.m_targetZDO) : null;
                if (target == null || target.GetOwner() == rpcData.m_senderPeerID)
                {
                    return;
                }

                TargetKind targetKind = GetTargetKind(target);
                if (!IsTargetKindEnabled(targetKind))
                {
                    return;
                }

                HitData hit = ReadHitData(rpcData.m_parameters);
                if (!IsPlayerChopOrPickaxeHit(hit, rpcData.m_senderPeerID))
                {
                    DebugLog($"Ignored {targetKind} damage handoff for {rpcData.m_targetZDO}: hit was not verified player chop/pickaxe damage.");
                    return;
                }

                ZNetPeer peer = ZNet.instance.GetPeer(rpcData.m_senderPeerID);
                if (peer == null || peer.m_characterID != hit.m_attacker)
                {
                    DebugLog($"Ignored {targetKind} damage handoff for {rpcData.m_targetZDO}: sender did not match attacker.");
                    return;
                }

                float now = Time.time;
                if (LastHandoffTimes.TryGetValue(rpcData.m_targetZDO, out float lastHandoff) &&
                    now - lastHandoff < ModConfig.TreeOwnershipHandoffCooldownSeconds.Value)
                {
                    return;
                }

                float dueTime = now + Mathf.Max(0.0f, ModConfig.TreeOwnershipHandoffDelaySeconds.Value);
                PendingHandoffs[rpcData.m_targetZDO] = new PendingHandoff(rpcData.m_senderPeerID, targetKind, dueTime);
                DebugLog($"Scheduled {targetKind} ownership handoff for {rpcData.m_targetZDO} to {rpcData.m_senderPeerID}.");
            }
            catch (Exception ex)
            {
                ServideSideTweaksPlugin.ModLogger.LogWarning($"Failed to schedule tree ownership handoff: {ex}");
            }
        }

        internal static void Update()
        {
            if (PendingHandoffs.Count == 0 || ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
            {
                return;
            }

            float now = Time.time;
            List<ZDOID> ready = new();
            foreach (KeyValuePair<ZDOID, PendingHandoff> entry in PendingHandoffs)
            {
                if (now >= entry.Value.DueTime)
                {
                    ready.Add(entry.Key);
                }
            }

            foreach (ZDOID zdoId in ready)
            {
                PendingHandoff handoff = PendingHandoffs[zdoId];
                PendingHandoffs.Remove(zdoId);
                ApplyHandoff(zdoId, handoff, now);
            }
        }

        private static void ApplyHandoff(ZDOID zdoId, PendingHandoff handoff, float now)
        {
            ZDO? target = ZDOMan.instance.GetZDO(zdoId);
            if (target == null)
            {
                return;
            }

            if (target.GetOwner() == handoff.Owner)
            {
                return;
            }

            if (ZNet.instance.GetPeer(handoff.Owner) == null)
            {
                return;
            }

            TargetKind currentKind = GetTargetKind(target);
            if (currentKind != handoff.TargetKind || !IsTargetKindEnabled(currentKind))
            {
                return;
            }

            target.SetOwner(handoff.Owner);
            ZDOMan.instance.ForceSendZDO(zdoId);
            LastHandoffTimes[zdoId] = now;
            DebugLog($"Applied {currentKind} ownership handoff for {zdoId} to {handoff.Owner}.");
        }

        private static HitData ReadHitData(ZPackage parameters)
        {
            parameters.SetPos(0);
            HitData hit = new();
            hit.Deserialize(ref parameters);
            parameters.SetPos(0);
            return hit;
        }

        private static bool IsPlayerChopOrPickaxeHit(HitData hit, long sender)
        {
            if (hit.m_hitType != HitData.HitType.PlayerHit || hit.m_attacker.IsNone() || hit.m_attacker.UserID != sender)
            {
                return false;
            }

            return hit.m_damage.m_chop > 0.0f || hit.m_damage.m_pickaxe > 0.0f;
        }

        private static bool IsTargetKindEnabled(TargetKind targetKind)
        {
            return targetKind switch
            {
                TargetKind.TreeBase => ModConfig.EnableTreeBaseOwnershipHandoff.Value,
                TargetKind.TreeLog => ModConfig.EnableTreeLogOwnershipHandoff.Value,
                _ => false
            };
        }

        private static TargetKind GetTargetKind(ZDO zdo)
        {
            ZNetView? instance = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (instance != null)
            {
                if (instance.GetComponent<TreeBase>() != null)
                {
                    return TargetKind.TreeBase;
                }

                if (instance.GetComponent<TreeLog>() != null)
                {
                    return TargetKind.TreeLog;
                }
            }

            GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            if (prefab == null)
            {
                return TargetKind.None;
            }

            if (prefab.GetComponent<TreeBase>() != null)
            {
                return TargetKind.TreeBase;
            }

            return prefab.GetComponent<TreeLog>() != null ? TargetKind.TreeLog : TargetKind.None;
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugTreeOwnershipHandoff.Value)
            {
                ServideSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private enum TargetKind
        {
            None,
            TreeBase,
            TreeLog
        }

        private readonly struct PendingHandoff
        {
            internal readonly long Owner;
            internal readonly TargetKind TargetKind;
            internal readonly float DueTime;

            internal PendingHandoff(long owner, TargetKind targetKind, float dueTime)
            {
                Owner = owner;
                TargetKind = targetKind;
                DueTime = dueTime;
            }
        }
    }
}
