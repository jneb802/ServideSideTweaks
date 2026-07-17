using HarmonyLib;
using ServerSideTweaks.Features.BossStones;
using UnityEngine;

namespace ServerSideTweaks.Patches
{
    [HarmonyPatch(typeof(Game), "RPC_DiscoverClosestLocation")]
    internal static class GameRpcDiscoverClosestLocationPatch
    {
        private static void Prefix(long sender, string name, Vector3 point, string pinName, int pinType, bool showMap, bool discoverAll)
        {
            BossLocationDiscoveryDiagnostics.LogDiscoverClosestLocationHandler(sender, name, point, pinName, pinType, showMap, discoverAll);
        }
    }
}
