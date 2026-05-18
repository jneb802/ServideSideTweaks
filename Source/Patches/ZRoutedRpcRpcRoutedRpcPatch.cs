using HarmonyLib;
using ServerSideTweaks.Features.Bosses;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    internal static class ZRoutedRpcRpcRoutedRpcPatch
    {
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
                ZRoutedRpc.RoutedRPCData rpcData = new();
                rpcData.Deserialize(pkg);
                pkg.SetPos(0);

                return !BossMessage.TryConsumeIncomingRoutedRpc(rpcData);
            }
            catch (System.Exception ex)
            {
                pkg.SetPos(0);
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to inspect incoming routed RPC: {ex}");
                return true;
            }
        }
    }
}
