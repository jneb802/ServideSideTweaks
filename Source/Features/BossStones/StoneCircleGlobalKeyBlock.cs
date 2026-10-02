using HarmonyLib;

namespace ServerSideTweaks.Features.BossStones
{
    internal static class StoneCircleGlobalKeyBlock
    {
        internal static bool IsEnabled()
        {
            return ModConfig.BlockStoneCircleGlobalKey.Value &&
                   ZNet.instance != null && ZNet.instance.IsServer();
        }

        internal static void Update()
        {
            ZoneSystem zoneSystem = ZoneSystem.instance;
            if (!IsEnabled() || zoneSystem == null || !zoneSystem.GetGlobalKey("stonecircle"))
            {
                return;
            }

            zoneSystem.GlobalKeyRemove("stonecircle");
            zoneSystem.SendGlobalKeys(0L);
            ServerSideTweaksPlugin.ModLogger.LogInfo("Removed blocked StoneCircle global key.");
        }
    }

    // Saved-world loading, starting keys, and SetGlobalKey RPCs all use this method.
    [HarmonyPatch(typeof(ZoneSystem), "GlobalKeyAdd")]
    internal static class ZoneSystemBlockStoneCircleGlobalKeyPatch
    {
        private static bool Prefix(string keyStr)
        {
            if (!StoneCircleGlobalKeyBlock.IsEnabled())
            {
                return true;
            }

            ZoneSystem.GetKeyValue(keyStr, out _, out GlobalKeys key);
            return key != GlobalKeys.StoneCircle;
        }
    }
}
