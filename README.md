# serverSideTweaks

Server-side Valheim tweaks for dedicated servers.

Author: warpalicious

## Features

- Reveals placed location icons per player instead of sending every placed icon to every connected player.
- Prevents boss summon, alert, and death center-screen messages from being relayed globally to other players.
- Gates configured boss-unlocked vendor items by per-player boss progress. Boss kills credit connected players within 64 meters of the player whose client reports the boss defeat global key.
- Prevents players from placing trophies on start-temple boss stones.
- Hands tree and log ownership to the player damaging them, after the current hit has finished, so later hits and likely final destruction are handled by the active chopper.
- Transfers door ownership to the player using the door before routing the vanilla door-use RPC.
- Transfers mine rock ownership to the player damaging the rock with a pickaxe before routing the hit RPC.
- Transfers beehive and sap collector ownership to the player extracting resources before routing `RPC_Extract`.
- Transfers fermenter ownership to the player adding mead base or tapping finished mead before routing the fermenter RPC.
- Experimental pickable ownership handoff that consumes vanilla pick RPCs, transfers ownership to the picker, then replays the pick after a short delay.
- Optional announcement boards: players can register supported sign commands and let the server rewrite those signs with live data.

## Prerequisites

- macOS with Valheim installed via Steam
- .NET SDK 8.0+ (`brew install dotnet`)
- [BepInEx for macOS](https://github.com/BepInEx/BepInEx/releases) installed in Valheim
- Publicized assemblies in `Managed/publicized_assemblies/`

## Quick Start

```bash
cd ServideSideTweaks
dotnet build
```

The built DLL will be in `bin/Debug/`. Install it on the Valheim server under `BepInEx/plugins/`.

## Configuration

Edit `Environment.props` if your Steam library is in a non-standard location. By default it uses `$HOME/Library/Application Support/Steam/steamapps/common/Valheim`.

Runtime config is written to `BepInEx/config/warpalicious.serverSideTweaks.cfg`.

| Section | Key | Default | Effect |
| --- | --- | --- | --- |
| LocationIcons | EnablePerPlayerLocationIcons | true | Reveals placed location icons per player instead of sending them to every connected player. |
| LocationIcons | LocationIconRevealDistance | 256 | Distance from a placed location icon required for that player to discover it. |
| LocationIcons | LocationIconDiscoveryFile | warpalicious.serverSideTweaks.locationIcons.tsv | Per-player location icon discovery file. Relative paths are resolved from `BepInEx/config`. |
| LocationIcons | DebugPerPlayerLocationIcons | false | Logs per-player location icon discovery and filtering decisions. |
| BossMessages | EnableBossMessageRelayBlock | true | Prevents boss summon, alert, and death center-screen messages from being relayed globally to other players. |
| BossMessages | DebugBossMessageRelayBlock | false | Logs blocked boss center-screen message relays. |
| VendorItems | EnableVendorItemsPerPlayer | true | Sends configured boss defeat global keys only to players recorded as having earned them. |
| VendorItems | VendorProgressGlobalKeys | defeated_eikthyr,defeated_gdking,defeated_bonemass,defeated_dragon,defeated_goblinking | Boss defeat global keys filtered per player. |
| VendorItems | VendorProgressFile | warpalicious.serverSideTweaks.vendorProgress.yaml | Per-player vendor progress YAML file. Relative paths are resolved from `BepInEx/config`. |
| BossStoneTrophies | EnableBossStoneTrophyPlacementBlock | true | Prevents players from placing trophies on start-temple boss stones. |
| TreeOwnership | EnableTreeBaseOwnershipHandoff | true | Standing tree damage schedules ownership handoff to the attacking player. |
| TreeOwnership | EnableTreeLogOwnershipHandoff | true | Fallen log damage schedules ownership handoff to the attacking player. |
| TreeOwnership | TreeOwnershipHandoffDelaySeconds | 0.25 | Delay before changing owner so the current damage RPC can finish first. |
| TreeOwnership | TreeOwnershipHandoffCooldownSeconds | 1.0 | Minimum time between ownership handoffs for the same tree or log. |
| TreeOwnership | DebugTreeOwnershipHandoff | false | Logs handoff decisions for testing. |
| DoorOwnership | EnableDoorOwnershipHandoff | true | Server transfers door ownership to the interacting player before routing `UseDoor`. |
| DoorOwnership | DebugDoorOwnershipHandoff | false | Logs door handoff decisions for testing. |
| MineRockOwnership | EnableMineRockOwnershipHandoff | true | Server transfers MineRock and MineRock5 ownership to the player making verified pickaxe damage before routing the hit RPC. |
| MineRockOwnership | DebugMineRockOwnershipHandoff | false | Logs MineRock handoff decisions for testing. |
| HarvestOwnership | EnableHarvestOwnershipHandoff | true | Server transfers beehive and sap collector ownership to the interacting player before routing `RPC_Extract`. |
| HarvestOwnership | DebugHarvestOwnershipHandoff | false | Logs beehive and sap collector handoff decisions for testing. |
| FermenterOwnership | EnableFermenterOwnershipHandoff | true | Server transfers fermenter ownership to the interacting player before routing `RPC_AddItem` and `RPC_Tap`. |
| FermenterOwnership | DebugFermenterOwnershipHandoff | false | Logs fermenter handoff decisions for testing. |
| PickableOwnership | EnablePickableOwnershipHandoff | true | Experimental. Server consumes pick RPCs, transfers ownership, and replays the pick to the picker. |
| PickableOwnership | PickableOwnershipReplayDelaySeconds | 0.35 | Delay before replaying the consumed pick RPC. |
| PickableOwnership | PickableOwnershipReplayAttempts | 2 | Maximum replay attempts for a consumed pick RPC. |
| PickableOwnership | PickableOwnershipReplayRetrySeconds | 0.35 | Delay between replay attempts. |
| PickableOwnership | DebugPickableOwnershipHandoff | false | Logs pickable handoff decisions for testing. |
| AnnouncementBoards | EnableAnnouncementBoards | false | Enables server-managed sign command boards. |
| AnnouncementBoards | MaxCharacters | 1800 | Maximum rich-text characters written to one board sign. |
| AnnouncementBoards | UpdateIntervalSeconds | 5 | Seconds between registered board refresh checks. This does not discover new signs. |
| AnnouncementBoards | SignCommand | !sign | Chat command players use to register nearby board signs. |
| AnnouncementBoards | SignScanRadius | 25 | Meters around the player scanned when `SignCommand` is used. |
| AnnouncementBoards | SignCommandCooldownSeconds | 15 | Minimum seconds between `SignCommand` uses per player. |
| AnnouncementBoards | RegistryFile | warpalicious.serverSideTweaks.announcementBoards.tsv | Registered board TSV file under `BepInEx/config`. |
| AnnouncementBoards | SignTextApiUrl | https://valheim-events.vercel.app/api/sign-text?server=praetoris-s6 | ValheimEvents API returning sign text JSON for leaderboard and player signs. |
| AnnouncementBoards | ResetDataFile | praetoris_resets.json | Reset state JSON file written by Cron Job. |
| AnnouncementBoards | ResetDataRefreshSeconds | 30 | Seconds between checks for changes to ResetDataFile. |
| AnnouncementBoards | ResetSignRefreshSeconds | 60 | Fallback seconds between reset sign refreshes; reset signs also refresh when ResetDataFile changes. |
| AnnouncementBoards | LeaderboardRefreshIntervalSeconds | 1800 | Seconds between leaderboard API checks. |
| AnnouncementBoards | PlayerSignRefreshIntervalSeconds | 600 | Seconds between player stat sign API checks. |
| AnnouncementBoards | MaxWritesPerUpdate | 20 | Maximum sign ZDO writes processed per server update frame. |
| AnnouncementBoards | LogMetrics | true | Logs compact announcement board performance metrics. |
| AnnouncementBoards | MetricsLogIntervalSeconds | 300 | Seconds between performance metric log lines when LogMetrics is true. |
| AnnouncementBoards | DebugAnnouncementBoards | false | Logs board scan and update decisions. |

## Announcement Boards

When enabled, a player can place a normal Valheim sign, write a supported board command on
the sign, then type `!sign` in chat while standing near it. The server consumes the `!sign`
chat message, scans nearby signs once, records supported signs in the registry file, and
replaces the sign text with generated server text. Board text is world state, so any player
who can see the sign can read the same message.

There is no automatic discovery scan during normal server updates. Existing registered
boards still refresh on their configured source intervals, and unchanged text is skipped.

Supported sign commands are `!leaderboard`, `!player`, and `!reset`. All options use
`key=value` parameters. `size=1` is the default, and numeric sizes are clamped from `0.1`
to `5`. `alignment=left` is the default; `center` and `right` are also supported. Names
with spaces can be quoted. Generated stat rows are marked no-wrap so each generated row
stays on its own line while scaling.

```text
!leaderboard board=deaths
!leaderboard board=deaths size=1.2 alignment=center
!player player=Taro
!player player=Taro stat=deaths size=1.1 alignment=right
!player player="Robin Goodfellow" stat=last-online alignment=left
!reset
!reset reset=copper size=2 alignment=center
```

Admin commands:

```text
sst_boards_scan
sst_boards_leaderboard_example [playerName]
sst_boards_sign_examples [nearPlayerName] [statsPlayerName]
```

## Vendor Progress File

Vendor progress is saved as a YAML file. The server reads the file from disk when sending vendor-related global keys and reloads it before recording new boss progress, so manual edits take effect without restarting the server.

```yaml
players:
  'Warponiius':
    playerId: 2123954456
    globalKeys:
      - defeated_eikthyr
      - defeated_gdking
      - defeated_bonemass
      - defeated_dragon
      - defeated_goblinking
```

If the YAML file does not exist yet, the server can read the old `warpalicious.serverSideTweaks.vendorProgress.tsv` file and writes future updates to the YAML file.

## Location Icon Discovery File

Location icon discoveries are saved as a tab-separated file with `playerName` and `zoneX:zoneY:locationPrefabName` columns.

## License

MIT
