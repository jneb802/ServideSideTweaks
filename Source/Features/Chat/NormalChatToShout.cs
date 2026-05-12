using System;
using UnityEngine;

namespace ServideSideTweaks.Features.Chat
{
    internal static class NormalChatToShout
    {
        private static readonly int SayHash = "Say".GetStableHashCode();
        private static readonly int ChatMessageHash = "ChatMessage".GetStableHashCode();

        internal static void TryConvert(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ModConfig.ConvertNormalChatToShout.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            if (rpcData.m_methodHash != SayHash || rpcData.m_targetZDO.IsNone())
            {
                return;
            }

            try
            {
                rpcData.m_parameters.SetPos(0);
                Talker.Type talkType = (Talker.Type)rpcData.m_parameters.ReadInt();
                if (talkType != Talker.Type.Normal)
                {
                    return;
                }

                UserInfo userInfo = new();
                userInfo.Deserialize(ref rpcData.m_parameters);
                string text = rpcData.m_parameters.ReadString();
                ZDOID characterId = rpcData.m_targetZDO;

                rpcData.m_targetZDO = ZDOID.None;
                rpcData.m_methodHash = ChatMessageHash;
                rpcData.m_parameters = BuildShoutParameters(GetChatPosition(characterId), userInfo, text);
            }
            catch (Exception ex)
            {
                ServideSideTweaksPlugin.ModLogger.LogWarning($"Failed to convert normal chat to shout: {ex}");
            }
        }

        private static ZPackage BuildShoutParameters(Vector3 position, UserInfo userInfo, string text)
        {
            ZPackage parameters = new();
            ZRpc.Serialize(new object[]
            {
                position,
                (int)Talker.Type.Shout,
                userInfo,
                text
            }, ref parameters);
            parameters.SetPos(0);
            return parameters;
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
    }
}
