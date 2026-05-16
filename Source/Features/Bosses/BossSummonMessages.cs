using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ServerSideTweaks.Features.Bosses
{
    internal static class BossSummonMessages
    {
        private static readonly List<PendingSummon> PendingSummons = new();

        internal static void Clear()
        {
            PendingSummons.Clear();
        }

        internal static void TrackSummon(OfferingBowl bowl, long senderPeerId, Vector3 spawnPoint)
        {
            if (!IsEnabled())
            {
                return;
            }

            try
            {
                ZNetPeer peer = ZNet.instance.GetPeer(senderPeerId);
                if (peer == null || !peer.IsReady())
                {
                    DebugLog($"Ignored boss summon tracking for sender {senderPeerId}: no ready peer.");
                    return;
                }

                BaseAI? bossAi = bowl.m_bossPrefab != null ? bowl.m_bossPrefab.GetComponent<BaseAI>() : null;
                string spawnMessage = bossAi != null ? bossAi.m_spawnMessage : "";
                if (string.IsNullOrEmpty(spawnMessage))
                {
                    DebugLog("Ignored boss summon tracking: boss prefab has no spawn message.");
                    return;
                }

                CleanupExpired();
                PendingSummons.Add(new PendingSummon
                {
                    SenderPeerId = senderPeerId,
                    SummonerPosition = peer.GetRefPos(),
                    SpawnPoint = spawnPoint,
                    BossPrefabName = bowl.m_bossPrefab != null ? Utils.GetPrefabName(bowl.m_bossPrefab) : "",
                    SpawnMessage = spawnMessage,
                    CreatedAt = Time.time,
                    MaxBossDistance = Mathf.Max(50.0f, bowl.m_spawnBossMaxDistance + 20.0f)
                });

                DebugLog($"Tracked boss summon by {senderPeerId} for message \"{spawnMessage}\".");
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to track boss summon message: {ex}");
            }
        }

        internal static bool TryReplaceSpawnMessage(BaseAI bossAi)
        {
            if (!IsEnabled() || string.IsNullOrEmpty(bossAi.m_spawnMessage))
            {
                return false;
            }

            try
            {
                ZNetView nview = bossAi.GetComponent<ZNetView>();
                Character character = bossAi.GetComponent<Character>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner() || character == null || !character.IsBoss())
                {
                    return false;
                }

                ZDO zdo = nview.GetZDO();
                if (zdo == null || zdo.GetLong(ZDOVars.s_spawnTime) != 0L)
                {
                    return false;
                }

                CleanupExpired();
                int summonIndex = FindMatchingSummon(bossAi);
                if (summonIndex < 0)
                {
                    return false;
                }

                PendingSummon summon = PendingSummons[summonIndex];
                PendingSummons.RemoveAt(summonIndex);
                SendMessageToNearbyPeers(summon, bossAi.m_spawnMessage);
                return true;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to replace boss summon message: {ex}");
                return false;
            }
        }

        private static int FindMatchingSummon(BaseAI bossAi)
        {
            string message = bossAi.m_spawnMessage;
            string prefabName = Utils.GetPrefabName(bossAi.gameObject);
            Vector3 bossPosition = bossAi.transform.position;

            int bestIndex = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < PendingSummons.Count; i++)
            {
                PendingSummon summon = PendingSummons[i];
                if (!string.Equals(summon.SpawnMessage, message, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(summon.BossPrefabName) &&
                    !string.Equals(summon.BossPrefabName, prefabName, StringComparison.Ordinal))
                {
                    continue;
                }

                float distance = Vector3.Distance(bossPosition, summon.SpawnPoint);
                if (distance > summon.MaxBossDistance || distance >= bestDistance)
                {
                    continue;
                }

                bestIndex = i;
                bestDistance = distance;
            }

            return bestIndex;
        }

        private static void SendMessageToNearbyPeers(PendingSummon summon, string message)
        {
            if (ZRoutedRpc.instance == null)
            {
                return;
            }

            float range = Mathf.Max(0.0f, ModConfig.BossSummonMessageRange.Value);
            int sent = 0;
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer == null || !peer.IsReady())
                {
                    continue;
                }

                bool isSummoner = peer.m_uid == summon.SenderPeerId;
                if (!isSummoner && Vector3.Distance(peer.GetRefPos(), summon.SummonerPosition) > range)
                {
                    continue;
                }

                ZRoutedRpc.instance.InvokeRoutedRPC(
                    peer.m_uid,
                    "ShowMessage",
                    (int)MessageHud.MessageType.Center,
                    message);
                sent++;
            }

            DebugLog($"Sent boss summon message \"{message}\" to {sent} nearby peer(s); original global message was suppressed.");
        }

        private static void CleanupExpired()
        {
            float now = Time.time;
            float maxAge = Mathf.Max(1.0f, ModConfig.BossSummonMessagePendingSeconds.Value);
            for (int i = PendingSummons.Count - 1; i >= 0; i--)
            {
                if (now - PendingSummons[i].CreatedAt > maxAge)
                {
                    DebugLog($"Expired pending boss summon for message \"{PendingSummons[i].SpawnMessage}\".");
                    PendingSummons.RemoveAt(i);
                }
            }
        }

        private static bool IsEnabled()
        {
            return ModConfig.EnableBossSummonMessageRange.Value == true &&
                ZNet.instance != null &&
                ZNet.instance.IsServer();
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugBossSummonMessageRange.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private struct PendingSummon
        {
            internal long SenderPeerId;
            internal Vector3 SummonerPosition;
            internal Vector3 SpawnPoint;
            internal string BossPrefabName;
            internal string SpawnMessage;
            internal float CreatedAt;
            internal float MaxBossDistance;
        }
    }

    [HarmonyPatch(typeof(OfferingBowl), "RPC_SpawnBoss")]
    internal static class OfferingBowlSpawnBossPatch
    {
        private static void Prefix(OfferingBowl __instance, ref bool __state)
        {
            __state = __instance.IsInvoking("DelayedSpawnBoss");
        }

        private static void Postfix(OfferingBowl __instance, long senderId, Vector3 point, bool __state)
        {
            if (!__state && __instance.IsInvoking("DelayedSpawnBoss"))
            {
                BossSummonMessages.TrackSummon(__instance, senderId, point);
            }
        }
    }

    [HarmonyPatch(typeof(BaseAI), "Awake")]
    internal static class BaseAIAwakePatch
    {
        private static void Prefix(BaseAI __instance, ref string? __state)
        {
            __state = null;
            if (!BossSummonMessages.TryReplaceSpawnMessage(__instance))
            {
                return;
            }

            __state = __instance.m_spawnMessage;
            __instance.m_spawnMessage = "";
        }

        private static void Postfix(BaseAI __instance, string? __state)
        {
            if (__state != null)
            {
                __instance.m_spawnMessage = __state;
            }
        }
    }
}
