using BepInEx.Configuration;

namespace ServerSideTweaks
{
    internal static class ModConfig
    {
        internal static ConfigEntry<bool> EnableTreeBaseOwnershipHandoff = null!;
        internal static ConfigEntry<bool> EnableTreeLogOwnershipHandoff = null!;
        internal static ConfigEntry<float> TreeOwnershipHandoffDelaySeconds = null!;
        internal static ConfigEntry<float> TreeOwnershipHandoffCooldownSeconds = null!;
        internal static ConfigEntry<bool> DebugTreeOwnershipHandoff = null!;
        internal static ConfigEntry<bool> EnableDoorOwnershipHandoff = null!;
        internal static ConfigEntry<bool> DebugDoorOwnershipHandoff = null!;
        internal static ConfigEntry<bool> EnablePickableOwnershipHandoff = null!;
        internal static ConfigEntry<float> PickableOwnershipReplayDelaySeconds = null!;
        internal static ConfigEntry<int> PickableOwnershipReplayAttempts = null!;
        internal static ConfigEntry<float> PickableOwnershipReplayRetrySeconds = null!;
        internal static ConfigEntry<bool> DebugPickableOwnershipHandoff = null!;
        internal static ConfigEntry<bool> EnableMineRockOwnershipHandoff = null!;
        internal static ConfigEntry<bool> DebugMineRockOwnershipHandoff = null!;
        internal static ConfigEntry<bool> EnableHarvestOwnershipHandoff = null!;
        internal static ConfigEntry<bool> DebugHarvestOwnershipHandoff = null!;
        internal static ConfigEntry<bool> EnableFermenterOwnershipHandoff = null!;
        internal static ConfigEntry<bool> DebugFermenterOwnershipHandoff = null!;
        internal static ConfigEntry<bool> EnableResetChatCommands = null!;
        internal static ConfigEntry<string> ResetDataFile = null!;
        internal static ConfigEntry<float> ResetDataRefreshSeconds = null!;
        internal static ConfigEntry<int> ResetChatMaxUpcomingEntries = null!;
        internal static ConfigEntry<bool> EnableBossStoneTrophyPlacementBlock = null!;

        internal static void Bind(ConfigFile config)
        {
            EnableTreeBaseOwnershipHandoff = config.Bind(
                "TreeOwnership",
                "EnableTreeBaseOwnershipHandoff",
                true,
                "When true, player damage to standing trees asks the server to hand ownership to the attacker after the current hit has finished.");

            EnableTreeLogOwnershipHandoff = config.Bind(
                "TreeOwnership",
                "EnableTreeLogOwnershipHandoff",
                true,
                "When true, player damage to fallen logs asks the server to hand ownership to the attacker after the current hit has finished.");

            TreeOwnershipHandoffDelaySeconds = config.Bind(
                "TreeOwnership",
                "TreeOwnershipHandoffDelaySeconds",
                0.25f,
                "Delay before applying a tree/log ownership handoff. This avoids changing owner during the same damage RPC.");

            TreeOwnershipHandoffCooldownSeconds = config.Bind(
                "TreeOwnership",
                "TreeOwnershipHandoffCooldownSeconds",
                1.0f,
                "Minimum time between ownership handoffs for the same tree/log ZDO.");

            DebugTreeOwnershipHandoff = config.Bind(
                "TreeOwnership",
                "DebugTreeOwnershipHandoff",
                false,
                "When true, logs tree/log ownership handoff decisions.");

            EnableDoorOwnershipHandoff = config.Bind(
                "DoorOwnership",
                "EnableDoorOwnershipHandoff",
                true,
                "When true, door use RPCs transfer door ownership to the interacting player and route the door action to that player.");

            DebugDoorOwnershipHandoff = config.Bind(
                "DoorOwnership",
                "DebugDoorOwnershipHandoff",
                false,
                "When true, logs door ownership handoff decisions.");

            EnablePickableOwnershipHandoff = config.Bind(
                "PickableOwnership",
                "EnablePickableOwnershipHandoff",
                true,
                "Experimental. When true, pickable RPCs are consumed by the server, ownership is handed to the picker, and the pick RPC is replayed to that picker after a short delay.");

            PickableOwnershipReplayDelaySeconds = config.Bind(
                "PickableOwnership",
                "PickableOwnershipReplayDelaySeconds",
                0.35f,
                "Delay before replaying a consumed pick RPC to the picker. This gives the owner update time to reach the client.");

            PickableOwnershipReplayAttempts = config.Bind(
                "PickableOwnership",
                "PickableOwnershipReplayAttempts",
                2,
                "Maximum number of replay attempts for a consumed pick RPC.");

            PickableOwnershipReplayRetrySeconds = config.Bind(
                "PickableOwnership",
                "PickableOwnershipReplayRetrySeconds",
                0.35f,
                "Delay between pick RPC replay attempts.");

            DebugPickableOwnershipHandoff = config.Bind(
                "PickableOwnership",
                "DebugPickableOwnershipHandoff",
                false,
                "When true, logs pickable ownership handoff decisions.");

            EnableMineRockOwnershipHandoff = config.Bind(
                "MineRockOwnership",
                "EnableMineRockOwnershipHandoff",
                true,
                "When true, player pickaxe damage to MineRock and MineRock5 objects transfers ownership to the attacker before routing the damage RPC.");

            DebugMineRockOwnershipHandoff = config.Bind(
                "MineRockOwnership",
                "DebugMineRockOwnershipHandoff",
                false,
                "When true, logs MineRock ownership handoff decisions.");

            EnableHarvestOwnershipHandoff = config.Bind(
                "HarvestOwnership",
                "EnableHarvestOwnershipHandoff",
                true,
                "When true, beehive and sap collector extract RPCs transfer ownership to the interacting player before routing.");

            DebugHarvestOwnershipHandoff = config.Bind(
                "HarvestOwnership",
                "DebugHarvestOwnershipHandoff",
                false,
                "When true, logs beehive and sap collector ownership handoff decisions.");

            EnableFermenterOwnershipHandoff = config.Bind(
                "FermenterOwnership",
                "EnableFermenterOwnershipHandoff",
                true,
                "When true, fermenter add-item and tap RPCs transfer ownership to the interacting player before routing.");

            DebugFermenterOwnershipHandoff = config.Bind(
                "FermenterOwnership",
                "DebugFermenterOwnershipHandoff",
                false,
                "When true, logs fermenter ownership handoff decisions.");

            EnableResetChatCommands = config.Bind(
                "ResetChatCommands",
                "EnableResetChatCommands",
                true,
                "When true, players can use chat commands like !resets and !resets copper to query Praetoris reset timing.");

            ResetDataFile = config.Bind(
                "ResetChatCommands",
                "ResetDataFile",
                "praetoris_resets.json",
                "Reset state JSON file written by Cron Job. Relative paths are resolved from the BepInEx config folder.");

            ResetDataRefreshSeconds = config.Bind(
                "ResetChatCommands",
                "ResetDataRefreshSeconds",
                5.0f,
                "How often the server checks the reset data file for changes.");

            ResetChatMaxUpcomingEntries = config.Bind(
                "ResetChatCommands",
                "ResetChatMaxUpcomingEntries",
                5,
                "Maximum upcoming reset entries shown by !resets with no argument.");

            EnableBossStoneTrophyPlacementBlock = config.Bind(
                "BossStoneTrophies",
                "EnableBossStoneTrophyPlacementBlock",
                true,
                "When true, prevents players from placing trophies on start-temple boss stones.");
        }
    }
}
