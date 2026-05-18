using System.Collections.Generic;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks.Features.Bosses
{
    internal static class BossMessage
    {
        private static readonly int ShowMessageHash = "ShowMessage".GetStableHashCode();

        private static readonly HashSet<string> BlockedBossMessages = new()
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

                if (messageType != MessageHud.MessageType.Center || !BlockedBossMessages.Contains(message))
                {
                    return RoutedRpcAction.Continue;
                }

                ZNetPeer senderPeer = ZNet.instance.GetPeer(rpcData.m_senderPeerID);
                DebugLog($"Suppressed global boss ShowMessage relay: msgID={rpcData.m_msgID}, sender={FormatPeer(senderPeer, rpcData.m_senderPeerID)}, message=\"{message}\".");
                return RoutedRpcAction.Consume;
            }
            catch (System.Exception ex)
            {
                rpcData.m_parameters.SetPos(0);
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to filter boss message: {ex}");
                return RoutedRpcAction.Continue;
            }
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
    }
}
