# serverSideTweaks

Server-side Valheim tweaks for dedicated servers.

Author: warpalicious

## Features

- Reveals placed location icons per player instead of sending every placed icon to every connected player.
- Forces the server to publish every connected player's map position, even when a player disables public position sharing, with an optional character-name exemption list for server administrators.
- Relays boss summon, alert, and death center-screen messages only to players near the boss.
- Gates configured boss-unlocked vendor items by per-player boss progress. Boss kills credit connected players within 64 meters of the player whose client reports the boss defeat global key.
- Prevents players from placing trophies on start-temple boss stones.

Object ownership is managed by Valheim and installed ownership mods such as NetworkPerformanceSystem (NPS). ServerSideTweaks does not assign or release object ownership. NPS is not a required dependency of this mod.

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
| PersistentEvents | AllowedBiomes | DeepNorth | Allowed biomes for new Jotun invasion centers when placement restrictions are enabled. |
| PersistentEvents | PrefabClearance | 100 | Ward exclusion radius in metres, also used for other protected prefabs. The event's maximum outer edge must stay outside this radius. |
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

Existing `DoorOwnership`, `HarvestOwnership`, `FermenterOwnership`, and `OwnershipHandoff` config sections are unused and can be removed. The earlier `TreeOwnership`, `PickableOwnership`, and `MineRockOwnership` sections are also unused.

### Jotun invasion biomes

`[PersistentEvents] AllowedBiomes = DeepNorth` limits new invasion centers to Deep North by default. This replaces the game's biome list, which excludes Deep North. For multiple biomes, use comma-separated game names, such as `Mountain, Plains`. `None` prevents new invasions while `EnablePlacementRestrictions` is enabled. The game's other placement rules and configured protected-object clearance still apply.

The existing config watcher is unchanged. Restart the server after editing settings if file-change notifications do not reload them, including in linked config folders. Existing invasions do not move or stop. When upgrading from a version without this setting, the default changes from Mountains and Plains to Deep North. Set `AllowedBiomes = Mountain, Plains` to retain the previous restriction.

`PrefabClearance = 100` keeps the event's maximum outer edge at least 100 metres from each ward or other configured protected prefab. Change this value to adjust the exclusion radius. For example, a 300-metre maximum event radius and 100-metre clearance require the center to be more than 400 metres away. Disabled and unloaded wards count. This setting does not change the ward's own build/access protection radius.

Live validation of this removal is pending. See [the validation plan](docs/ownership-removal-validation.md).

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
