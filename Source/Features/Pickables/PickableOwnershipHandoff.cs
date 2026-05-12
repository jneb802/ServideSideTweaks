using System;
using System.Collections.Generic;
using UnityEngine;
using ServideSideTweaks.Infrastructure;
using ServideSideTweaks.Infrastructure.Routing;

namespace ServideSideTweaks.Features.Pickables
{
    internal static class PickableOwnershipHandoff
    {
        private static readonly Dictionary<ZDOID, PendingPick> PendingPicks = new();

        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("RPC_Pick", HandlePick);
        }

        private static RoutedRpcAction HandlePick(ZRoutedRpc.RoutedRPCData rpcData)
        {
            return TryConsume(rpcData) ? RoutedRpcAction.Consume : RoutedRpcAction.Continue;
        }

        private static bool TryConsume(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ModConfig.EnablePickableOwnershipHandoff.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
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

                ZNetPeer peer = ZNet.instance.GetPeer(rpcData.m_senderPeerID);
                if (peer == null)
                {
                    return false;
                }

                ZDO? target = zdoMan.GetZDO(rpcData.m_targetZDO);
                if (target == null || !ZdoComponentLookup.HasComponent<Pickable>(target))
                {
                    return false;
                }

                int bonus = ReadBonus(rpcData.m_parameters);
                target.SetOwner(rpcData.m_senderPeerID);
                zdoMan.ForceSendZDO(rpcData.m_senderPeerID, rpcData.m_targetZDO);

                float firstReplayTime = Time.time + Mathf.Max(0.0f, ModConfig.PickableOwnershipReplayDelaySeconds.Value);
                int attempts = Mathf.Max(1, ModConfig.PickableOwnershipReplayAttempts.Value);
                PendingPicks[rpcData.m_targetZDO] = new PendingPick(rpcData.m_senderPeerID, bonus, firstReplayTime, attempts);
                DebugLog($"Consumed pickable RPC for {rpcData.m_targetZDO}; owner {rpcData.m_senderPeerID}, bonus {bonus}, attempts {attempts}.");
                return true;
            }
            catch (Exception ex)
            {
                ServideSideTweaksPlugin.ModLogger.LogWarning($"Failed to consume pickable ownership handoff: {ex}");
                return false;
            }
        }

        internal static void Update()
        {
            if (PendingPicks.Count == 0 || ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || ZRoutedRpc.instance == null)
            {
                return;
            }

            float now = Time.time;
            List<ZDOID> ready = new();
            foreach (KeyValuePair<ZDOID, PendingPick> entry in PendingPicks)
            {
                if (now >= entry.Value.NextReplayTime)
                {
                    ready.Add(entry.Key);
                }
            }

            foreach (ZDOID zdoId in ready)
            {
                ReplayPick(zdoId, PendingPicks[zdoId], now);
            }
        }

        private static void ReplayPick(ZDOID zdoId, PendingPick pendingPick, float now)
        {
            ZDO? target = ZDOMan.instance.GetZDO(zdoId);
            if (target == null || target.GetBool(ZDOVars.s_picked))
            {
                PendingPicks.Remove(zdoId);
                return;
            }

            if (ZNet.instance.GetPeer(pendingPick.Owner) == null)
            {
                PendingPicks.Remove(zdoId);
                return;
            }

            if (!ZdoComponentLookup.HasComponent<Pickable>(target))
            {
                PendingPicks.Remove(zdoId);
                return;
            }

            if (target.GetOwner() != pendingPick.Owner)
            {
                target.SetOwner(pendingPick.Owner);
            }

            ZDOMan.instance.ForceSendZDO(pendingPick.Owner, zdoId);
            ZRoutedRpc.instance.InvokeRoutedRPC(pendingPick.Owner, zdoId, "RPC_Pick", pendingPick.Bonus);
            DebugLog($"Replayed pickable RPC for {zdoId} to {pendingPick.Owner}; attempts left {pendingPick.AttemptsRemaining - 1}.");

            int attemptsRemaining = pendingPick.AttemptsRemaining - 1;
            if (attemptsRemaining <= 0)
            {
                PendingPicks.Remove(zdoId);
                return;
            }

            float retryDelay = Mathf.Max(0.05f, ModConfig.PickableOwnershipReplayRetrySeconds.Value);
            PendingPicks[zdoId] = pendingPick.WithNextReplay(now + retryDelay, attemptsRemaining);
        }

        private static int ReadBonus(ZPackage parameters)
        {
            parameters.SetPos(0);
            int bonus = parameters.ReadInt();
            parameters.SetPos(0);
            return bonus;
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugPickableOwnershipHandoff.Value)
            {
                ServideSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private readonly struct PendingPick
        {
            internal readonly long Owner;
            internal readonly int Bonus;
            internal readonly float NextReplayTime;
            internal readonly int AttemptsRemaining;

            internal PendingPick(long owner, int bonus, float nextReplayTime, int attemptsRemaining)
            {
                Owner = owner;
                Bonus = bonus;
                NextReplayTime = nextReplayTime;
                AttemptsRemaining = attemptsRemaining;
            }

            internal PendingPick WithNextReplay(float nextReplayTime, int attemptsRemaining)
            {
                return new PendingPick(Owner, Bonus, nextReplayTime, attemptsRemaining);
            }
        }
    }
}
