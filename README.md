# Servide Side Tweaks

Server-side Valheim tweaks for dedicated servers.

Author: warpalicious

## Features

- Converts normal chat messages into vanilla shout-style delivery on the server, so players can type normally instead of using `/s` for server-wide chat.
- Adds server-handled reset chat commands. Players can type `!resets` for upcoming reset times or `!resets copper` for the last and next copper reset, with data read from Cron Job's local reset file.
- Hands tree and log ownership to the player damaging them, after the current hit has finished, so later hits and likely final destruction are handled by the active chopper.
- Transfers door ownership to the player using the door before routing the vanilla door-use RPC.
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

Runtime config is written to `BepInEx/config/warpalicious.ServideSideTweaks.cfg`.

| Section | Key | Default | Effect |
| --- | --- | --- | --- |
| Chat | ConvertNormalChatToShout | true | Server forwards normal chat as vanilla shout messages. |
| ResetChatCommands | EnableResetChatCommands | true | Enables `!resets` chat commands. |
| ResetChatCommands | ResetDataFile | praetoris_resets.json | Reset state JSON file written by Cron Job. Relative paths are resolved from `BepInEx/config`. |
| ResetChatCommands | ResetDataRefreshSeconds | 5 | How often the server checks the reset data file for changes. |
| ResetChatCommands | ResetChatMaxUpcomingEntries | 5 | Maximum upcoming reset entries shown by `!resets`. |
| TreeOwnership | EnableTreeBaseOwnershipHandoff | true | Standing tree damage schedules ownership handoff to the attacking player. |
| TreeOwnership | EnableTreeLogOwnershipHandoff | true | Fallen log damage schedules ownership handoff to the attacking player. |
| TreeOwnership | TreeOwnershipHandoffDelaySeconds | 0.25 | Delay before changing owner so the current damage RPC can finish first. |
| TreeOwnership | TreeOwnershipHandoffCooldownSeconds | 1.0 | Minimum time between ownership handoffs for the same tree or log. |
| TreeOwnership | DebugTreeOwnershipHandoff | false | Logs handoff decisions for testing. |
| DoorOwnership | EnableDoorOwnershipHandoff | true | Server transfers door ownership to the interacting player before routing `UseDoor`. |
| DoorOwnership | DebugDoorOwnershipHandoff | false | Logs door handoff decisions for testing. |
| PickableOwnership | EnablePickableOwnershipHandoff | false | Experimental. Server consumes pick RPCs, transfers ownership, and replays the pick to the picker. |
| PickableOwnership | PickableOwnershipReplayDelaySeconds | 0.35 | Delay before replaying the consumed pick RPC. |
| PickableOwnership | PickableOwnershipReplayAttempts | 2 | Maximum replay attempts for a consumed pick RPC. |
| PickableOwnership | PickableOwnershipReplayRetrySeconds | 0.35 | Delay between replay attempts. |
| PickableOwnership | DebugPickableOwnershipHandoff | false | Logs pickable handoff decisions for testing. |

## Reset Chat Commands

Cron Job writes `praetoris_resets.json` in the BepInEx config folder when a tracked reset command executes. ServerSideTweaks reads that local file, caches the latest valid contents, and answers chat commands from memory.

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
