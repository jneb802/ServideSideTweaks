using System;
using System.Collections.Generic;
using UnityEngine;
using ServideSideTweaks.Infrastructure.Routing;

namespace ServideSideTweaks.Features.Chat
{
    internal static class NormalChatToShout
    {
        private const float SenderEchoDedupSeconds = 1.0f;
        private static readonly Dictionary<SenderEchoKey, float> LastSenderEchoTimes = new();

        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("Say", HandleSay);
        }

        private static RoutedRpcAction HandleSay(ZRoutedRpc.RoutedRPCData rpcData)
        {
            return TryConvert(rpcData) ? RoutedRpcAction.Consume : RoutedRpcAction.Continue;
        }

        private static bool TryConvert(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ModConfig.ConvertNormalChatToShout.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return false;
            }

            if (rpcData.m_targetZDO.IsNone())
            {
                return false;
            }

            try
            {
                rpcData.m_parameters.SetPos(0);
                Talker.Type talkType = (Talker.Type)rpcData.m_parameters.ReadInt();
                if (talkType != Talker.Type.Normal)
                {
                    return false;
                }

                UserInfo userInfo = new();
                userInfo.Deserialize(ref rpcData.m_parameters);
                string text = rpcData.m_parameters.ReadString();
                ZDOID characterId = rpcData.m_targetZDO;
                Vector3 position = GetChatPosition(characterId);

                SendShout(rpcData.m_targetPeerID, position, userInfo, text);
                TrySendSenderEcho(rpcData.m_senderPeerID, position, userInfo, text);

                return true;
            }
            catch (Exception ex)
            {
                ServideSideTweaksPlugin.ModLogger.LogWarning($"Failed to convert normal chat to shout: {ex}");
                return false;
            }
        }

        private static void SendShout(long targetPeerId, Vector3 position, UserInfo userInfo, string text)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(
                targetPeerId,
                "ChatMessage",
                position,
                (int)Talker.Type.Shout,
                userInfo,
                text);
        }

        private static void TrySendSenderEcho(long senderPeerId, Vector3 position, UserInfo userInfo, string text)
        {
            if (senderPeerId == 0L)
            {
                return;
            }

            float now = Time.time;
            PruneSenderEchoes(now);

            SenderEchoKey key = new(senderPeerId, text);
            if (LastSenderEchoTimes.TryGetValue(key, out float lastEchoTime) &&
                now - lastEchoTime < SenderEchoDedupSeconds)
            {
                return;
            }

            LastSenderEchoTimes[key] = now;
            SendShout(senderPeerId, position, userInfo, text);
        }

        private static void PruneSenderEchoes(float now)
        {
            if (LastSenderEchoTimes.Count == 0)
            {
                return;
            }

            List<SenderEchoKey> expired = new();
            foreach (KeyValuePair<SenderEchoKey, float> entry in LastSenderEchoTimes)
            {
                if (now - entry.Value >= SenderEchoDedupSeconds)
                {
                    expired.Add(entry.Key);
                }
            }

            foreach (SenderEchoKey key in expired)
            {
                LastSenderEchoTimes.Remove(key);
            }
        }

        private static Vector3 GetChatPosition(ZDOID characterId)
        {
            ZDO? zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(characterId) : null;
            if (zdo == null)
            {
                return Vector3.zero;
            }

            ZNetView? view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            Character? character = view != null ? view.GetComponent<Character>() : null;
            return character != null ? character.GetHeadPoint() : zdo.GetPosition() + Vector3.up * 1.8f;
        }

        private readonly struct SenderEchoKey : IEquatable<SenderEchoKey>
        {
            private readonly long _senderPeerId;
            private readonly string _text;

            internal SenderEchoKey(long senderPeerId, string text)
            {
                _senderPeerId = senderPeerId;
                _text = text;
            }

            public bool Equals(SenderEchoKey other)
            {
                return _senderPeerId == other._senderPeerId && string.Equals(_text, other._text, StringComparison.Ordinal);
            }

            public override bool Equals(object? obj)
            {
                return obj is SenderEchoKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_senderPeerId.GetHashCode() * 397) ^ (_text != null ? _text.GetHashCode() : 0);
                }
            }
        }
    }
}
