# serverSideTweaks

Server-side Valheim tweaks for dedicated servers.

Author: warpalicious

## Features

- Reveals placed location icons per player instead of sending every placed icon to every connected player.
- Forces the server to publish every connected player's map position, even when a player disables public position sharing, with an optional character-name exemption list for server administrators.
- Relays boss summon, alert, and death center-screen messages only to players near the boss.
- Gates configured boss-unlocked vendor items by per-player boss progress. Boss kills credit connected players within 64 meters of the player whose client reports the boss defeat global key.
- Prevents players from placing trophies on start-temple boss stones.
- Transfers door ownership to the player using the door before routing the vanilla door-use RPC.
- Transfers beehive and sap collector ownership to the player extracting resources before routing `RPC_Extract`.
- Transfers fermenter ownership to the player adding mead base or tapping finished mead before routing the fermenter RPC.

Server signs now live in the standalone [ServerSigns](https://github.com/jneb802/ServerSigns) mod.

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
| PlayerMapPositions | ForcePublicPlayerPositions | true | Server always publishes every connected player's map position. |
| PlayerMapPositions | ForcePublicPlayerPositionExemptAdminCharacterNames | empty | Comma-separated character names that may keep their positions private while Valheim recognizes the connected accounts as server administrators. |
| PlayerMapPositions | DebugForcePublicPlayerPositions | false | Logs when the server overrides a private player position. |
| LocationIcons | EnablePerPlayerLocationIcons | true | Reveals placed location icons per player instead of sending them to every connected player. |
| LocationIcons | LocationIconRevealDistance | 256 | Distance from a placed location icon required for that player to discover it. |
| LocationIcons | LocationIconDiscoveryFile | warpalicious.serverSideTweaks.locationIcons.tsv | Per-player location icon discovery file. Relative paths are resolved from `BepInEx/config`. |
| LocationIcons | DebugPerPlayerLocationIcons | false | Logs per-player location icon discovery and filtering decisions. |
| BossMessages | EnableBossMessageRelayBlock | true | Relays boss summon, alert, and death center-screen messages only to nearby players. |
| BossMessages | BossMessageRange | 120 | Maximum distance from the boss at which a player receives boss center-screen messages. |
| BossMessages | DebugBossMessageRelayBlock | false | Logs boss center-screen message relay decisions. |
| VendorItems | EnableVendorItemsPerPlayer | true | Sends configured boss defeat global keys only to players recorded as having earned them. |
| VendorItems | VendorProgressGlobalKeys | defeated_eikthyr,defeated_gdking,defeated_bonemass,defeated_dragon,defeated_goblinking | Boss defeat global keys filtered per player. |
| VendorItems | VendorProgressFile | warpalicious.serverSideTweaks.vendorProgress.yaml | Per-player vendor progress YAML file. Relative paths are resolved from `BepInEx/config`. |
| BossStoneTrophies | EnableBossStoneTrophyPlacementBlock | true | Prevents players from placing trophies on start-temple boss stones. |
| DoorOwnership | EnableDoorOwnershipHandoff | true | Server transfers door ownership to the interacting player before routing `UseDoor`. |
| DoorOwnership | DebugDoorOwnershipHandoff | false | Logs door handoff decisions for testing. |
| HarvestOwnership | EnableHarvestOwnershipHandoff | true | Server transfers beehive and sap collector ownership to the interacting player before routing `RPC_Extract`. |
| HarvestOwnership | DebugHarvestOwnershipHandoff | false | Logs beehive and sap collector handoff decisions for testing. |
| FermenterOwnership | EnableFermenterOwnershipHandoff | true | Server transfers fermenter ownership to the interacting player before routing `RPC_AddItem` and `RPC_Tap`. |
| FermenterOwnership | DebugFermenterOwnershipHandoff | false | Logs fermenter handoff decisions for testing. |
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
