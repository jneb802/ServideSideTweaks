using System;
using System.Collections.Generic;
using ServerSideTweaks.Infrastructure.Routing;
using UnityEngine;

namespace ServerSideTweaks.Features.Bosses
{
    internal static class BossMessage
    {
        private static readonly int ShowMessageHash = "ShowMessage".GetStableHashCode();

        private static readonly HashSet<string> LegacyBossMessages = new()
        {
            "$event_boss02_start",
            "$event_boss02_end",
            "$enemy_boss_bonemass_spawnmessage",
            "$enemy_boss_bonemass_deathmessage",
            "$enemy_boss_dragon_spawnmessage",
            "$enemy_boss_dragon_deathmessage",
            "$enemy_boss_goblinking_spawnmessage",
            "$enemy_boss_goblinking_deathmessage",
            "$enemy_boss_queen_alertmessage",
            "$enemy_boss_queen_deathmessage",
            "$enemy_boss_fader_alertmessage",
            "$enemy_boss_fader_deathmessage",
        };

        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("ShowMessage", HandleShowMessage);
        }

        internal static bool TryConsumeIncomingRoutedRpc(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (rpcData.m_methodHash != ShowMessageHash)
            {
                return false;
            }

            return HandleShowMessage(rpcData) == RoutedRpcAction.Consume;
        }

        private static RoutedRpcAction HandleShowMessage(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!IsEnabled())
            {
                return RoutedRpcAction.Continue;
            }

            if (rpcData.m_targetPeerID != ZRoutedRpc.Everybody)
            {
                return RoutedRpcAction.Continue;
            }

            try
            {
                rpcData.m_parameters.SetPos(0);
                MessageHud.MessageType messageType = (MessageHud.MessageType)rpcData.m_parameters.ReadInt();
                string message = rpcData.m_parameters.ReadString();
                rpcData.m_parameters.SetPos(0);

                if (messageType != MessageHud.MessageType.Center || !IsBossMessage(message))
                {
                    return RoutedRpcAction.Continue;
                }

                ZNetPeer senderPeer = ZNet.instance.GetPeer(rpcData.m_senderPeerID);
                Vector3 origin = senderPeer != null ? senderPeer.GetRefPos() : Vector3.zero;
                bool hasOrigin = senderPeer != null;
                if (TryFindBossPosition(message, origin, hasOrigin, out Vector3 bossPosition))
                {
                    origin = bossPosition;
                    hasOrigin = true;
                }

                int relayed = hasOrigin ? RelayToNearbyPeers(senderPeer, origin, messageType, message) : 0;
                DebugLog($"Replaced global boss ShowMessage relay: msgID={rpcData.m_msgID}, sender={FormatPeer(senderPeer, rpcData.m_senderPeerID)}, message=\"{message}\", origin={FormatPosition(origin, hasOrigin)}, nearbyRecipients={relayed}.");
                return RoutedRpcAction.Consume;
            }
            catch (System.Exception ex)
            {
                rpcData.m_parameters.SetPos(0);
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to filter boss message: {ex}");
                return RoutedRpcAction.Continue;
            }
        }

        private static bool IsBossMessage(string message)
        {
            return LegacyBossMessages.Contains(message) ||
                message.StartsWith("$enemy_boss_", StringComparison.Ordinal);
        }

        private static bool TryFindBossPosition(string message, Vector3 referencePosition, bool hasReferencePosition, out Vector3 bossPosition)
        {
            bossPosition = Vector3.zero;
            float closestDistance = float.MaxValue;
            bool found = false;

            foreach (BaseAI bossAi in BaseAI.BaseAIInstances)
            {
                if (bossAi == null || !MatchesMessage(bossAi, message))
                {
                    continue;
                }

                Character character = bossAi.GetComponent<Character>();
                if (character == null || !character.IsBoss())
                {
                    continue;
                }

                Vector3 candidatePosition = bossAi.transform.position;
                float distance = hasReferencePosition
                    ? Vector3.Distance(referencePosition, candidatePosition)
                    : 0.0f;
                if (found && distance >= closestDistance)
                {
                    continue;
                }

                bossPosition = candidatePosition;
                closestDistance = distance;
                found = true;
            }

            return found;
        }

        private static bool MatchesMessage(BaseAI bossAi, string message)
        {
            return string.Equals(bossAi.m_spawnMessage, message, StringComparison.Ordinal) ||
                string.Equals(bossAi.m_alertedMessage, message, StringComparison.Ordinal) ||
                string.Equals(bossAi.m_deathMessage, message, StringComparison.Ordinal);
        }

        private static int RelayToNearbyPeers(
            ZNetPeer? senderPeer,
            Vector3 origin,
            MessageHud.MessageType messageType,
            string message)
        {
            float range = Mathf.Max(0.0f, ModConfig.BossMessageRange.Value);
            int relayed = 0;
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer == null || !peer.IsReady() || peer == senderPeer)
                {
                    continue;
                }

                if (Vector3.Distance(peer.GetRefPos(), origin) > range)
                {
                    continue;
                }

                ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", (int)messageType, message);
                relayed++;
            }

            return relayed;
        }

        private static bool IsEnabled()
        {
            return ModConfig.EnableBossMessageRelayBlock.Value == true &&
                ZNet.instance != null &&
                ZNet.instance.IsServer();
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugBossMessageRelayBlock.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private static string FormatPeer(ZNetPeer? peer, long fallbackPeerId)
        {
            if (peer == null)
            {
                return $"unknown ({fallbackPeerId})";
            }

            string playerName = string.IsNullOrWhiteSpace(peer.m_playerName) ? "<unknown>" : peer.m_playerName;
            return $"{playerName} ({peer.m_uid})";
        }

        private static string FormatPosition(Vector3 position, bool hasPosition)
        {
            return hasPosition ? $"({position.x:F1}, {position.y:F1}, {position.z:F1})" : "unknown";
        }
    }
}
