# Persistent event placement validation

Validated on 2026-09-24 with Valdev and Valnet client 02. The candidate ran on the dedicated server only. No production deployment was performed.

## Environment

- Season 8 version 8.0.26 was the deployed baseline. Version 8.0.27 was staged but not deployed.
- Server test profile: `persistent-events-s8-8-0-26`, copied from `ward-count-s8-8-0-26`.
- Client test profile: `persistent-events-s8-8.0.26`, copied from `ward-count-s8-8.0.26`.
- Six differing package DLLs were aligned to installed production files. Duplicate server DLLs were removed from the test copy. All 31 DLL names shared by the final server/client profiles had matching SHA-256 hashes, including the proof helper.
- Valdev game version: `l-1.0.12`. Client game version: `l-1.0.15`. The client completed the connection and mod compatibility checks. This does not prove behavior on other server builds.
- Used an isolated copy of `mwlPortIconTest`, a separate character, and the [test helper](../validation/PersistentEventProof/README.md). The helper was allowed only in the copied server profile. The feature DLL was absent from the client.

## Results

Release builds of the mod and helper passed with zero warnings and zero errors. `git diff --check` passed.

The final server run passed 216 assertions:

- 100 real placement searches succeeded: 38 Mountain centers and 62 Plains centers.
- A stored, unloaded, disabled ward blocked placement.
- The exact exclusion boundary was rejected, including a 2,000-metre vertical separation. A point one metre beyond the boundary was accepted.
- A rejected candidate caused the real search to select another position beyond the ward clearance.
- Changing the prefab list to `piece_workbench` changed the protected objects. An empty list retained biome protection.
- Disabling the feature preserved vanilla behavior. Vanilla rejections and unrelated event definitions were unchanged.
- A fully protected search exhausted vanilla's attempt limit and failed without placing an event at a forbidden position.

The connected client then called the game's normal event request through the helper. The server selected a Mountain center and verified it against the stored prefab exclusions. Both sides reported:

```text
event ID: 0
center: (2001.69, 203.52, 3134.36)
radius: 152.1773
```

The built-in event console command was blocked on the dedicated-server client in version 1.0.15. The helper sent the normal request; it did not choose the position or bypass the placement filter. This covers event placement and replication, not destruction of the natural triggering object, a full event completion, or a 40-player load test.

## Log review

No feature exceptions were found. The failed search in the rule tests was intentional. The initial helper connection was rejected by the mod allowlist; adding the helper to the isolated test profile fixed it, and the final compatibility check passed.

The mod set produced unrelated warnings: Linux shader/material and headless video failures, asset registration messages, Deathlink fallback configuration, and FastLink sample-address lookups. During the final connected window, StarLevelSystem reported object count changes during location resets, and PraetorisClient reported retries for older queued telemetry uploads. These remain baseline issues; the run does not establish clean rendering, location resets, or telemetry delivery.

## Scope and evidence

The biome restriction covers the event center. The event area can cross a biome border. Prefab clearance covers the maximum possible event radius plus the configured distance. Existing events, later ward construction, and enemies moving outside the event area are not controlled by this feature.

Local evidence is retained under `/Users/benjmarston/Develop/artifacts/persistent-event-validation/`:

- `server-rule-tests.txt`: 216 assertions.
- `server-live-event.txt` and `client-live-event.txt`: matching live event state.
- `client-final-status.txt`: connected, in-world client.
- `server-final.log` and `client-final.log`: full diagnostic windows.
- `server-final-dll-sha256.json` and `client-final-dll-sha256.json`: deployed DLL hashes.

Raw logs are kept outside the repository because they contain connection and player identifiers.

The client logged out and its original profile was restored. Valnet client 02 was confirmed off. Valdev was stopped and its original settings and profile links were restored. Both device leases were released. Test profiles and evidence were retained.
