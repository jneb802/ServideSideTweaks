using HarmonyLib;
using ServerSideTweaks.Features.BossStones;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZDOExtraData), nameof(ZDOExtraData.Set), typeof(ZDOID), typeof(int), typeof(int))]
    internal static class ZDOExtraDataSetIntPatch
    {
        private static bool Prefix(ZDOID zid, int hash, int value, ref bool __result)
        {
            if (BossStoneTrophyPlacementBlock.AllowZdoIntSet(zid, hash, value))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }
}
