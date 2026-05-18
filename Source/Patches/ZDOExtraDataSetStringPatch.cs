using HarmonyLib;
using ServerSideTweaks.Features.BossStones;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZDOExtraData), nameof(ZDOExtraData.Set), typeof(ZDOID), typeof(int), typeof(string))]
    internal static class ZDOExtraDataSetStringPatch
    {
        private static bool Prefix(ZDOID zid, int hash, string value, ref bool __result)
        {
            if (BossStoneTrophyPlacementBlock.AllowZdoStringSet(zid, hash, value))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }
}
