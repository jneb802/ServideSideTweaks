using HarmonyLib;
using ServerSideTweaks.Features.BossStones;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
    internal static class ZRoutedRpcHandleRoutedRpcPatch
    {
        private static bool Prefix(ZRoutedRpc.RoutedRPCData data)
        {
            return BossStoneTrophyPlacementBlock.ProcessIncomingRoutedRpc(data);
        }
    }
}
