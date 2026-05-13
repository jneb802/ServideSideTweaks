# Combined Hetzner Validation Plan

## Objective

Validate three features together on the current Hetzner Praetoris enRoute test setup:

- `serverSideTweaks` ownership handoffs.
- `CronJob` reset tracking written to `praetoris_resets.json` and shown through `!resets` chat commands.
- enRoute RPC routing improvements.

Use the current enRoute profiles instead of creating new isolated profiles.

## Current Profiles

Confirmed active profiles during preparation:

- Mac client: `enroute-rpc-routing-client`
- Hetzner server: `enroute-rpc-routing-server`
- Windows PC client: `enroute-rpc-routing-client`

Confirmed validation support mods:

- Mac client has `Azumatt-FastLink`, `Azumatt-XRayVision`, and `JereKuusela-Server_devcommands`.
- Windows client has `Azumatt-FastLink`, `Azumatt-XRayVision`, and `JereKuusela-Server_devcommands`.
- Hetzner server has `Azumatt-XRayVision`, `CronJob`, `JereKuusela-Server_devcommands`, `serverSideTweaks`, `local-EnRoute`, and DiscordConnector.

Do not switch profiles unless we intentionally roll back after the combined test pass.

Client profiles intentionally do not include `local-EnRoute`, `CronJob`, or `serverSideTweaks`.

## Owner Inspection

Use `Azumatt-XRayVision` on both clients and the server.

- Thunderstore: https://thunderstore.io/c/valheim/p/Azumatt/XRayVision/
- It shows `Owner` hover text from `view.m_zdo.GetOwner()`.
- It should be used to capture before/after evidence for door, tree, log, and pickable ownership.

Fallback only if XRayVision is not usable: `JereKuusela-ESP` with custom text using `<owner>`.

## Build Artifacts

Build `CronJob`:

```bash
cd /Users/benjmarston/Develop/valheim-cron_job
dotnet build CronJob.csproj
```

Expected artifact:

```text
/Users/benjmarston/Develop/valheim-cron_job/bin/Debug/CronJob.dll
```

Build `serverSideTweaks` from `main`:

```bash
cd /Users/benjmarston/Develop/ServideSideTweaks
dotnet build ServideSideTweaks.csproj
```

Expected artifact:

```text
/Users/benjmarston/Develop/ServideSideTweaks/bin/Debug/serverSideTweaks.dll
```

Before deployment, record both checksums:

```bash
shasum -a 256 /Users/benjmarston/Develop/valheim-cron_job/bin/Debug/CronJob.dll
shasum -a 256 /Users/benjmarston/Develop/ServideSideTweaks/bin/Debug/serverSideTweaks.dll
```

Prepared artifacts:

```text
7e8c572a4df7a59bc62327994637e92d14c3827c3f3a49783210e41a718f0f84  /Users/benjmarston/Develop/valheim-cron_job/bin/Debug/CronJob.dll
df00bd1df830bd8a0edacceb6ba7a822bdb4439ce4e39e9a3068072107a10a71  /Users/benjmarston/Develop/ServideSideTweaks/bin/Debug/serverSideTweaks.dll
```

## Server Safety

Use mmcli-agent only for server lifecycle actions.

Check status and player count before deploy or restart:

```bash
API_KEY="<redacted>"
ssh warp@praetoris "curl -s -H 'X-API-Key: $API_KEY' http://localhost:9877/api/v1/status"
```

Confirm active plugin path:

```bash
ssh warp@praetoris "readlink /home/warp/valheim/BepInEx/plugins"
```

Back up existing reset files/config:

```bash
ssh warp@praetoris "cp -a /home/warp/valheim/BepInEx/config/cron.yaml /home/warp/valheim/BepInEx/config/cron.yaml.pre-combined-validation 2>/dev/null || true"
ssh warp@praetoris "cp -a /home/warp/valheim/BepInEx/config/praetoris_resets.json /home/warp/valheim/BepInEx/config/praetoris_resets.json.pre-combined-validation 2>/dev/null || true"
```

## Deploy Test DLLs

Copy both feature DLLs into the active enRoute server profile:

```bash
PROFILE_PLUGINS=$(ssh warp@praetoris "readlink /home/warp/valheim/BepInEx/plugins")

ssh warp@praetoris "mkdir -p $PROFILE_PLUGINS/CronJob $PROFILE_PLUGINS/serverSideTweaks"

scp /Users/benjmarston/Develop/valheim-cron_job/bin/Debug/CronJob.dll \
  warp@praetoris:$PROFILE_PLUGINS/CronJob/

scp /Users/benjmarston/Develop/ServideSideTweaks/bin/Debug/serverSideTweaks.dll \
  warp@praetoris:$PROFILE_PLUGINS/serverSideTweaks/
```

Restart through mmcli-agent after checking player count:

```bash
ssh warp@praetoris "curl -s -X POST -H 'X-API-Key: $API_KEY' http://localhost:9877/api/v1/restart"
```

Confirm load:

```bash
ssh warp@praetoris "grep -iE 'Cron Job|serverSideTweaks|EnRoute|error|exception' /home/warp/valheim/BepInEx/LogOutput.log | tail -120"
```

Expected:

- `CronJob` loads.
- `serverSideTweaks` loads.
- enRoute loads.
- No missing dependency errors.
- No relevant startup exceptions.

## Server Config

For ownership testing:

```ini
[TreeOwnership]
EnableTreeBaseOwnershipHandoff = true
EnableTreeLogOwnershipHandoff = true
DebugTreeOwnershipHandoff = true

[DoorOwnership]
EnableDoorOwnershipHandoff = true
DebugDoorOwnershipHandoff = true

[PickableOwnership]
EnablePickableOwnershipHandoff = false
DebugPickableOwnershipHandoff = true
```

Enable pickables only after chat, doors, trees, logs, reset chat, and enRoute pass:

```ini
[PickableOwnership]
EnablePickableOwnershipHandoff = true
```

For reset chat testing, confirm or set:

```ini
[ResetChatCommands]
EnableResetChatCommands = true
ResetDataRefreshSeconds = 10
```

Use the actual config key names generated by the merged `serverSideTweaks` build if they differ.

## Seeded Reset File Test

Seed a known reset file first. This validates `serverSideTweaks` file reading before CronJob writes anything.

Path:

```text
/home/warp/valheim/BepInEx/config/praetoris_resets.json
```

Seed content:

```json
{
  "generated_at": "2026-05-13T00:00:00Z",
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

In-game commands:

```text
!resets
!resets copper
!resets list
!resets badname
```

Expected:

- `!resets` returns upcoming reset entries.
- `!resets copper` returns Copper Node Reset with last/next timing.
- `!resets list` returns known reset keys.
- Invalid key returns a useful unknown reset message.
- The original `!resets...` chat message does not broadcast to other players.

## File Reload Test

While the server is running, edit `praetoris_resets.json` and change `copper.next`.

Wait at least `ResetDataRefreshSeconds` plus a few seconds.

Run:

```text
!resets copper
```

Expected:

- Response reflects the changed file without restarting the server.

## Live CronJob Write Test

This stage is required for full pass. It proves the real chain works:

```text
CronJob executes reset command
  -> CronJob writes praetoris_resets.json
  -> serverSideTweaks reloads file
  -> !resets returns that new data
```

Use the Hetzner test server only.

1. Back up current cron config:

```bash
ssh warp@praetoris "cp /home/warp/valheim/BepInEx/config/cron.yaml /home/warp/valheim/BepInEx/config/cron.yaml.pre-live-reset-test"
```

2. Add a temporary cron job scheduled a few minutes in the future. Use a real low-blast-radius reset command on the test server:

```yaml
- command: vegetation_reset rock4_copper biomes=BlackForest count=1 terrain=0 maxDistance=1000 safeZones=1 start
  schedule: "<minute> <hour> * * *"
  log: true
```

3. Restart or reload CronJob config as needed.

4. Watch logs until the scheduled command runs:

```bash
ssh warp@praetoris "tail -f /home/warp/valheim/BepInEx/LogOutput.log"
```

Expected log includes:

```text
Executing: vegetation_reset rock4_copper ...
```

5. Validate reset file was written:

```bash
ssh warp@praetoris "cat /home/warp/valheim/BepInEx/config/praetoris_resets.json"
```

Expected:

- `copper.last` is near the current UTC time.
- `copper.next` is about 3 days after `last`.
- `label` is `Copper Node Reset`.

6. In-game:

```text
!resets copper
!resets
```

Expected:

- `!resets copper` shows Copper Node Reset.
- Last reflects the command that just ran.
- Next reflects the next calculated copper reset.
- Copper appears in the upcoming list if it is within the top configured entries.

7. Restore original cron config:

```bash
ssh warp@praetoris "mv /home/warp/valheim/BepInEx/config/cron.yaml.pre-live-reset-test /home/warp/valheim/BepInEx/config/cron.yaml"
```

8. Restart or reload CronJob config again.

## Ownership Test Cases

Use Mac and Windows clients together.

1. Windows second-client join
   - Action: join from `windowspc` with a separate development character.
   - Expected: both clients are present on the server.

2. Door ownership handoff
   - Action: Windows owns the area or interacts with a door first, then Mac opens the same door.
   - Expected: XRayVision shows the door owner changes to the Mac player, and the door response is immediate.

3. Tree ownership handoff
   - Action: Windows owns the area, then Mac chops a standing tree.
   - Expected: after the configured delay, XRayVision or debug logs show the tree owner changes to Mac.

4. Log ownership handoff
   - Action: repeat with fallen logs.
   - Expected: owner changes to the active chopper before later hits or final destruction.

5. Pickable ownership handoff
   - Action: enable pickable handoff, then Mac picks a berry/mushroom/crop in an area owned by Windows.
   - Expected: owner changes to Mac, the pick succeeds once, and drops are not duplicated.

## enRoute Test Cases

Run the enRoute validation scenario against the same server profile after the DLLs are loaded.

Expected:

- enRoute-specific RPC routing behavior works with `serverSideTweaks` routed RPC dispatcher active.
- No `ZRoutedRpc` handler exceptions appear in server or client logs.
- Ownership and reset chat commands still work after enRoute actions.

Record the exact enRoute commands/actions used in the evidence notes for the run.

## Client Join

Mac:

```bash
cd /Users/benjmarston/Develop/valheimCLI/CLI
dotnet run -- --launch --connect 178.156.172.16:2456 --password warpalicious
```

Use a development character only.

Windows PC:

- Use the active `enroute-rpc-routing-client` profile.
- Use a separate development character and the Windows Steam account.
- Join the same Hetzner server.

## Evidence To Collect

- Build output and checksums for both DLLs.
- Server BepInEx load lines for CronJob, serverSideTweaks, and enRoute.
- Server BepInEx error scan after each test stage.
- `praetoris_resets.json` before seed, after seed, after reload edit, and after live CronJob write.
- In-game screenshots of `!resets`, `!resets copper`, `!resets list`, and invalid key.
- XRayVision screenshots or recordings showing owner before and after each ownership handoff.
- Mac and Windows client logs only if there is a client-side error.

## Rollback

Restore cron config:

```bash
ssh warp@praetoris "mv /home/warp/valheim/BepInEx/config/cron.yaml.pre-combined-validation /home/warp/valheim/BepInEx/config/cron.yaml 2>/dev/null || true"
```

Restore reset file:

```bash
ssh warp@praetoris "mv /home/warp/valheim/BepInEx/config/praetoris_resets.json.pre-combined-validation /home/warp/valheim/BepInEx/config/praetoris_resets.json 2>/dev/null || true"
```

Remove temporary deployed DLLs only if they should not remain installed:

```bash
PROFILE_PLUGINS=$(ssh warp@praetoris "readlink /home/warp/valheim/BepInEx/plugins")
ssh warp@praetoris "rm -rf $PROFILE_PLUGINS/CronJob $PROFILE_PLUGINS/serverSideTweaks"
```

Restart through mmcli-agent after rollback if loaded DLLs or cron config changed.

## Pass Criteria

The combined pass succeeds when:

- Both DLLs load on Hetzner with no errors.
- enRoute still works with the `serverSideTweaks` routed RPC dispatcher active.
- Ownership handoffs can be observed with XRayVision from Mac and Windows.
- `praetoris_resets.json` is read from server config.
- `!resets`, `!resets copper`, and `!resets list` work in-game.
- Invalid reset names are handled cleanly.
- File changes are picked up without restart.
- A real CronJob-executed reset command updates `praetoris_resets.json`.
- `!resets copper` reads the CronJob-written updated state.
