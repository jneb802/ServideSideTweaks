using HarmonyLib;
using ServerSideTweaks.Features.BossStones;
using ServerSideTweaks.Features.Bosses;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    internal static class ZRoutedRpcRpcRoutedRpcPatch
    {
        private static readonly int DiscoverClosestLocationHash = "RPC_DiscoverClosestLocation".GetStableHashCode();
        private static readonly int DiscoverLocationResponseHash = "RPC_DiscoverLocationResponse".GetStableHashCode();
        private static readonly int ShowMessageHash = "ShowMessage".GetStableHashCode();
        private static readonly int RequestOwnHash = "RPC_RequestOwn".GetStableHashCode();
        private static readonly int SetVisualItemHash = "SetVisualItem".GetStableHashCode();

        [HarmonyPriority(Priority.First)]
        [HarmonyBefore("redseiko.valheim.enroute")]
        private static bool Prefix(ZRoutedRpc __instance, ZPackage pkg)
        {
            if (!__instance.m_server)
            {
                return true;
            }

            try
            {
                pkg.SetPos(0);
                if (!RequiresInspection(pkg))
                {
                    return true;
                }

                ZRoutedRpc.RoutedRPCData rpcData = new();
                rpcData.Deserialize(pkg);
                pkg.SetPos(0);

                BossLocationDiscoveryDiagnostics.LogIncomingRoutedRpc(rpcData);
                if (BossLocationDiscoveryDiagnostics.TryHandleServerDiscoveryRequest(__instance, rpcData))
                {
                    return false;
                }

                BossStoneTrophyPlacementBlock.NotifyBlockedInteraction(rpcData);
                if (BossStoneTrophyPlacementBlock.TryConsumeVisualItem(rpcData))
                {
                    return false;
                }
                if (BossMessage.TryConsumeIncomingRoutedRpc(rpcData))
                {
                    return false;
                }

                return true;
            }
            catch (System.Exception ex)
            {
                pkg.SetPos(0);
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to inspect incoming routed RPC: {ex}");
                return true;
            }
        }

        private static bool RequiresInspection(ZPackage pkg)
        {
            // Match RoutedRPCData's wire header without reading/copying its payload.
            // Both the existing inspection and the next RPC handler expect offset zero.
            long targetPeerId;
            ZDOID targetZdo;
            int methodHash;
            try
            {
                pkg.ReadLong(); // Message ID.
                pkg.ReadLong(); // Sender ID.
                targetPeerId = pkg.ReadLong();
                targetZdo = pkg.ReadZDOID();
                methodHash = pkg.ReadInt();
            }
            finally
            {
                pkg.SetPos(0);
            }

            if (methodHash == DiscoverClosestLocationHash)
            {
                return true;
            }

            if (ModConfig.DebugBossLocationDiscovery.Value &&
                (methodHash == DiscoverLocationResponseHash ||
                 methodHash == RequestOwnHash || methodHash == SetVisualItemHash))
            {
                return true;
            }

            if (ModConfig.EnableBossMessageRelayBlock.Value &&
                methodHash == ShowMessageHash && targetPeerId == ZRoutedRpc.Everybody)
            {
                return true;
            }

            if (ModConfig.EnableBossStoneTrophyPlacementBlock.Value && !targetZdo.IsNone() &&
                (methodHash == RequestOwnHash || methodHash == SetVisualItemHash))
            {
                return true;
            }

            return false;
        }
    }
}
