using HarmonyLib;
using ServerSideTweaks.Features.BossStones;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner))]
    internal static class ZDOSetOwnerPatch
    {
        private static bool Prefix(ZDO __instance, long uid)
        {
            return BossStoneTrophyPlacementBlock.AllowOwnerChange(__instance, uid);
        }
    }

    [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal))]
    internal static class ZDOSetOwnerInternalPatch
    {
        private static void Prefix(ZDO __instance, ref long uid)
        {
            BossStoneTrophyPlacementBlock.NormalizeOwner(__instance, ref uid);
        }
    }
}
