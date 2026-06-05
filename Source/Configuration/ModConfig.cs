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
        internal static ConfigEntry<bool> EnablePerPlayerLocationIcons = null!;
        internal static ConfigEntry<float> LocationIconRevealDistance = null!;
        internal static ConfigEntry<string> LocationIconDiscoveryFile = null!;
        internal static ConfigEntry<bool> DebugPerPlayerLocationIcons = null!;
        internal static ConfigEntry<bool> EnableBossMessageRelayBlock = null!;
        internal static ConfigEntry<bool> DebugBossMessageRelayBlock = null!;
        internal static ConfigEntry<bool> EnableVendorItemsPerPlayer = null!;
        internal static ConfigEntry<string> VendorProgressGlobalKeys = null!;
        internal static ConfigEntry<string> VendorProgressFile = null!;
        internal static ConfigEntry<bool> EnableBossStoneTrophyPlacementBlock = null!;
        internal static ConfigEntry<bool> EnableValheimEnforcerKickAlerts = null!;
        internal static ConfigEntry<string> ValheimEnforcerKickAlertBotUrl = null!;
        internal static ConfigEntry<string> ValheimEnforcerBotApiKey = null!;
        internal static ConfigEntry<bool> DebugValheimEnforcerKickAlerts = null!;
        internal static ConfigEntry<bool> EnableServerSigns = null!;
        internal static ConfigEntry<int> ServerSignMaxCharacters = null!;
        internal static ConfigEntry<float> ServerSignUpdateIntervalSeconds = null!;
        internal static ConfigEntry<string> ServerSignCommand = null!;
        internal static ConfigEntry<float> ServerSignScanRadius = null!;
        internal static ConfigEntry<float> ServerSignCommandCooldownSeconds = null!;
        internal static ConfigEntry<string> ServerSignRegistryFile = null!;
        internal static ConfigEntry<string> ServerSignTextApiUrl = null!;
        internal static ConfigEntry<string> ServerSignValheimEventsApiKey = null!;
        internal static ConfigEntry<string> ServerSignResetDataFile = null!;
        internal static ConfigEntry<float> ServerSignResetDataRefreshSeconds = null!;
        internal static ConfigEntry<float> ServerSignResetSignRefreshSeconds = null!;
        internal static ConfigEntry<float> ServerSignLeaderboardRefreshIntervalSeconds = null!;
        internal static ConfigEntry<float> ServerSignPlayerSignRefreshIntervalSeconds = null!;
        internal static ConfigEntry<int> ServerSignMaxWritesPerUpdate = null!;
        internal static ConfigEntry<bool> ServerSignLogMetrics = null!;
        internal static ConfigEntry<float> ServerSignMetricsLogIntervalSeconds = null!;
        internal static ConfigEntry<bool> DebugServerSigns = null!;

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

            EnablePerPlayerLocationIcons = config.Bind(
                "LocationIcons",
                "EnablePerPlayerLocationIcons",
                true,
                "When true, placed location icons are revealed per player instead of being sent to every connected player.");

            LocationIconRevealDistance = config.Bind(
                "LocationIcons",
                "LocationIconRevealDistance",
                256.0f,
                "Distance from a placed location icon required for that player to discover it.");

            LocationIconDiscoveryFile = config.Bind(
                "LocationIcons",
                "LocationIconDiscoveryFile",
                "warpalicious.serverSideTweaks.locationIcons.tsv",
                "Per-player location icon discovery file. Relative paths are resolved from the BepInEx config folder.");

            DebugPerPlayerLocationIcons = config.Bind(
                "LocationIcons",
                "DebugPerPlayerLocationIcons",
                false,
                "When true, logs per-player location icon discovery and filtering decisions.");

            EnableBossMessageRelayBlock = config.Bind(
                "BossMessages",
                "EnableBossMessageRelayBlock",
                true,
                "When true, the server does not relay boss summon, alert, and death center-screen messages to other players.");

            DebugBossMessageRelayBlock = config.Bind(
                "BossMessages",
                "DebugBossMessageRelayBlock",
                false,
                "When true, logs blocked boss center-screen message relays.");

            EnableVendorItemsPerPlayer = config.Bind(
                "VendorItems",
                "EnableVendorItemsPerPlayer",
                true,
                "When true, configured boss defeat global keys are sent only to players recorded as having earned them.");

            VendorProgressGlobalKeys = config.Bind(
                "VendorItems",
                "VendorProgressGlobalKeys",
                "defeated_eikthyr,defeated_gdking,defeated_bonemass,defeated_dragon,defeated_goblinking",
                "Comma-separated global keys that should require per-player boss progress when sent to clients.");

            VendorProgressFile = config.Bind(
                "VendorItems",
                "VendorProgressFile",
                "warpalicious.serverSideTweaks.vendorProgress.yaml",
                "Per-player vendor progress YAML file. Relative paths are resolved from the BepInEx config folder.");

            EnableBossStoneTrophyPlacementBlock = config.Bind(
                "BossStoneTrophies",
                "EnableBossStoneTrophyPlacementBlock",
                true,
                "When true, prevents players from placing trophies on start-temple boss stones.");

            EnableValheimEnforcerKickAlerts = config.Bind(
                "ValheimEnforcer",
                "EnableKickAlerts",
                false,
                "When true, sends a Discord alert when ValheimEnforcer rejects a player for a mod mismatch.");

            ValheimEnforcerKickAlertBotUrl = config.Bind(
                "ValheimEnforcer",
                "KickAlertBotUrl",
                "",
                "Praetoris bot API URL for ValheimEnforcer mod mismatch alerts.");

            ValheimEnforcerBotApiKey = config.Bind(
                "ValheimEnforcer",
                "BotApiKey",
                "",
                "API key sent to the Praetoris bot alert endpoint in the X-API-Key header.");

            DebugValheimEnforcerKickAlerts = config.Bind(
                "ValheimEnforcer",
                "DebugKickAlerts",
                false,
                "When true, logs ValheimEnforcer kick alert decisions and API results.");

            EnableServerSigns = config.Bind(
                "ServerSigns",
                "EnableServerSigns",
                false,
                "When true, players can register supported sign commands with the server sign system.");

            ServerSignMaxCharacters = config.Bind(
                "ServerSigns",
                "MaxCharacters",
                1800,
                "Maximum rich-text characters written to one server sign.");

            ServerSignUpdateIntervalSeconds = config.Bind(
                "ServerSigns",
                "UpdateIntervalSeconds",
                5.0f,
                "Seconds between registered sign refresh checks. This does not scan for new signs.");

            ServerSignCommand = config.Bind(
                "ServerSigns",
                "SignCommand",
                "!sign",
                "Chat command players use after placing sign commands. The server scans nearby signs once and registers supported signs.");

            ServerSignScanRadius = config.Bind(
                "ServerSigns",
                "SignScanRadius",
                25.0f,
                "Meters around the player scanned when SignCommand is used.");

            ServerSignCommandCooldownSeconds = config.Bind(
                "ServerSigns",
                "SignCommandCooldownSeconds",
                15.0f,
                "Minimum seconds between SignCommand uses per player.");

            ServerSignRegistryFile = config.Bind(
                "ServerSigns",
                "RegistryFile",
                "warpalicious.serverSideTweaks.serverSigns.json",
                "Registered sign JSON file. Relative paths are resolved from the BepInEx config folder.");

            ServerSignTextApiUrl = config.Bind(
                "ServerSigns",
                "SignTextApiUrl",
                "https://valheim-events.vercel.app/api/sign-text?server=praetoris-s6",
                "HTTP URL returning ValheimEvents sign text JSON for !leaderboard and !player signs.");

            ServerSignValheimEventsApiKey = config.Bind(
                "ServerSigns",
                "ValheimEventsApiKey",
                "",
                "Shared secret sent to the ValheimEvents API in the X-API-Key header.");

            ServerSignResetDataFile = config.Bind(
                "ServerSigns",
                "ResetDataFile",
                "praetoris_resets.json",
                "Reset state JSON file written by Cron Job. Relative paths are resolved from the BepInEx config folder.");

            ServerSignResetDataRefreshSeconds = config.Bind(
                "ServerSigns",
                "ResetDataRefreshSeconds",
                30.0f,
                "Seconds between checks for changes to ResetDataFile.");

            ServerSignResetSignRefreshSeconds = config.Bind(
                "ServerSigns",
                "ResetSignRefreshSeconds",
                60.0f,
                "Fallback seconds between refreshes for registered reset signs. Reset signs also refresh when ResetDataFile changes.");

            ServerSignLeaderboardRefreshIntervalSeconds = config.Bind(
                "ServerSigns",
                "LeaderboardRefreshIntervalSeconds",
                1800.0f,
                "Seconds between leaderboard API checks. The API data normally changes once per day.");

            ServerSignPlayerSignRefreshIntervalSeconds = config.Bind(
                "ServerSigns",
                "PlayerSignRefreshIntervalSeconds",
                600.0f,
                "Seconds between API refreshes for registered player stat signs.");

            ServerSignMaxWritesPerUpdate = config.Bind(
                "ServerSigns",
                "MaxWritesPerUpdate",
                20,
                "Maximum server sign ZDO writes processed per server update frame.");

            ServerSignLogMetrics = config.Bind(
                "ServerSigns",
                "LogMetrics",
                true,
                "When true, logs compact server sign performance metrics on MetricsLogIntervalSeconds.");

            ServerSignMetricsLogIntervalSeconds = config.Bind(
                "ServerSigns",
                "MetricsLogIntervalSeconds",
                300.0f,
                "Seconds between server sign performance metric log lines when LogMetrics is true.");

            DebugServerSigns = config.Bind(
                "ServerSigns",
                "DebugServerSigns",
                false,
                "When true, logs server sign scans and updates.");
        }
    }
}
