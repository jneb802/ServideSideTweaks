using HarmonyLib;
using ServerSideTweaks.Features.BossStones;

namespace ServerSideTweaks.Patches
{
    // Network deserialization uses Add rather than the local-write Set method.
    [HarmonyPatch(typeof(ZDOExtraData), nameof(ZDOExtraData.Add), typeof(ZDOID), typeof(int), typeof(int))]
    internal static class ZDOExtraDataAddIntPatch
    {
        private static bool Prefix(ZDOID zid, int hash, int value)
        {
            return BossStoneTrophyPlacementBlock.AllowZdoIntSet(zid, hash, value);
        }
    }
}
