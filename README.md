# serverSideTweaks

Server-side Valheim tweaks for dedicated servers.

Author: warpalicious

## Features

- Adds server-handled reset chat commands. Players can type `!resets` for upcoming reset times or `!resets copper` for the last and next copper reset, with data read from Cron Job's local reset file.
- Limits boss summon center-screen messages to players near the summoning player.
- Hands tree and log ownership to the player damaging them, after the current hit has finished, so later hits and likely final destruction are handled by the active chopper.
- Transfers door ownership to the player using the door before routing the vanilla door-use RPC.
- Transfers mine rock ownership to the player damaging the rock with a pickaxe before routing the hit RPC.
- Transfers beehive and sap collector ownership to the player extracting resources before routing `RPC_Extract`.
- Transfers fermenter ownership to the player adding mead base or tapping finished mead before routing the fermenter RPC.
- Experimental pickable ownership handoff that consumes vanilla pick RPCs, transfers ownership to the picker, then replays the pick after a short delay.

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
| ResetChatCommands | EnableResetChatCommands | true | Enables `!resets` chat commands. |
| ResetChatCommands | ResetDataFile | praetoris_resets.json | Reset state JSON file written by Cron Job. Relative paths are resolved from `BepInEx/config`. |
| ResetChatCommands | ResetDataRefreshSeconds | 5 | How often the server checks the reset data file for changes. |
| ResetChatCommands | ResetChatMaxUpcomingEntries | 5 | Maximum upcoming reset entries shown by `!resets`. |
| BossSummonMessages | EnableBossSummonMessageRange | true | Sends boss summon center-screen messages only to nearby players. |
| BossSummonMessages | BossSummonMessageRange | 120 | Maximum distance from the summoning player for a player to receive the boss summon message. |
| BossSummonMessages | BossSummonMessagePendingSeconds | 45 | How long a summon attempt can wait for the matching boss spawn message. |
| BossSummonMessages | DebugBossSummonMessageRange | false | Logs boss summon message range decisions. |
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

## Reset Chat Commands

Cron Job writes `praetoris_resets.json` in the BepInEx config folder when a tracked reset command executes. serverSideTweaks reads that local file, caches the latest valid contents, and answers chat commands from memory.

Supported commands:

```text
!resets
!resets copper
!resets silver
!resets swamp
!resets list
```

Expected reset file:

```json
{
  "resets": {
    "copper": {
      "label": "Copper Node Reset",
      "last": "2026-05-12T00:00:00Z",
      "next": "2026-05-15T00:00:00Z",
      "interval_seconds": 259200
    }
  }
}
```

## License

MIT
