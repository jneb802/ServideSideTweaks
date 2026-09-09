# Valheim 1.0 feature validation

## Environment

- Server: isolated OVHcloud `valheim-1.0.service`, Valheim `l-1.0.7`, game port 5900.
- ServerSideTweaks: `main` commit `89f580a`, version 1.1.12.
- Other server plugins: PlayerTracker, CronJob, MaxPlayerCount, and the tested ValheimRcon compatibility build.
- Client: Valnet client 01, `ptb-testing`, with a separate `SstProbe0909` character. The existing `Developer` save contains Odev and is used for the comparison without earned boss credit.
- A temporary, separately built [feature probe](../validation/FeatureProbe/README.md) observes state and invokes normal game interaction methods. It is excluded from the shipped mod.
- Valnet client 02 was already using another test profile and was not changed. Transfers between two player-owned clients are not covered.

## Results

| Feature | Observed result | Coverage limit |
| --- | --- | --- |
| Forced public map positions | Client requested private sharing. The server repeatedly logged an override and reported `Public position: True`. | A second player's map display was not checked. |
| Location icon delivery | The test character initially received StartTemple. After travelling to Haldor at `(1788.88, 31.74, -172.21)`, it also received Vendor_BlackForest. Odev later received both icons. The source deliberately shares placed Haldor/Hildir icons. | This checks delivery and the trader exception, not per-player discovery. No discovery file was created. The 256-metre boundary and boss-location discovery remain untested. |
| Boss-message suppression | A client-generated Bonemass spawn-message broadcast reached the server filter and was logged as suppressed. An ordinary control message was sent separately. | No second recipient was available to check its screen. |
| Boss progress / vendor keys | The server recorded Eikthyr and Elder credit for the test character. A fresh Elder key reached the client immediately. Recorded credit survived reconnecting. Odev joined without either key while both existed globally, confirming separation between these characters. | Actual trader inventory and multi-player credit within/outside 64 metres were not checked. See the stale-key issue below. |
| Starting-temple trophies | An Elder trophy attached to the previously empty starting-temple stone with the block enabled. The stored item hash changed from 0 to `-7767225`. | This is a failure. See the integer-field issue below. |
| Door ownership | A client-owned door opened normally. With server ownership established immediately before normal interaction, the interaction returned true but the state did not change and no transfer handler log appeared. | Server-addressed route fails this check. Transfer from another player is untested. |
| Pickable ownership | A client-owned mushroom was picked normally. A fresh server-owned mushroom accepted interaction but remained unpicked. | Server-addressed route fails this check. Replay between two players is untested. |
| Beehive / sap ownership | Client-owned sap extraction changed level 3 to 0. Server-owned built beehive and sap collector interactions left their seeded level at 3. | Server-addressed route fails this check. Natural resource accumulation was not tested. |
| Fermenter ownership | Adding minor-health mead base to a client-owned fermenter succeeded. A server-owned fermenter seeded with completed mead accepted tapping but retained its content. | Server-addressed route fails this check. Natural fermentation under a roof was not tested. |
| Standing tree / log ownership | Damage calls were sent after establishing server ownership. No delayed handoff handler log appeared. | The server-addressed path was exercised; preservation of the current hit and transfers between two players are untested. |
| Mining ownership | A low pickaxe hit was sent to a staged copper rock after establishing server ownership. No handoff handler log appeared. | Multi-area mining and transfers between two players are untested. |
| ValheimEnforcer integrations | Not tested: ValheimEnforcer is absent from this server. | Kick alerts are disabled. The group-policy flag alone does not install its required dependency. |

## Findings

### Trophy placement uses integer data in 1.0

The 1.0 `ItemStand.UpdateAttach` writes the trophy's integer prefab hash to `ZDOVars.s_item` and sends `SetVisualItem` with an integer first argument. `BossStoneTrophyPlacementBlock` only blocks string writes through `ZDOExtraDataSetStringPatch`, so the current placement bypasses the block.

The same interaction produced two caught `EndOfStreamException` warnings in `BossLocationDiscovery` diagnostics. Those diagnostics still decode the old message payload. The server remained running, but this is not a clean compatibility pass.

The attachment remained after switching to Odev. Cleanup through the stand's normal removal method returned the stored item hash to 0 before logout.

Reproduction: use an empty starting-temple boss stone, a matching new trophy inventory item, and the stand's normal `UseItem` method. Verify the item's integer hash before and after, rather than reading the obsolete string field. The probe's initial generic interaction selected the boss-stone component instead of its child stand; that attempt is not evidence for this result.

### Server-addressed object interactions skip ownership handlers

`ZRoutedRpcRouteRpcPatch` dispatches ownership handlers through `RouteRPC`. An incoming message addressed to the server is handled locally instead of passing through that forwarding path. `ZRoutedRpcRpcRoutedRpcPatch` currently handles boss-message and boss-stone concerns but does not dispatch ownership handlers.

The probe established server ownership on both server and client before calling the ordinary interaction or damage method. Server logs confirmed setup. Client logs confirmed the server owner immediately before the action. No ownership-handler log followed. The later return of ownership to the active client is normal ownership reassignment, not proof of a successful mod handoff.

This is a functional gap in the tested server-owned case. It is not evidence that transfers between two player-owned clients fail, nor proof that the gap first appeared in Valheim 1.0.

### Credit for an already-global boss key is not immediately sent

Set `defeated_eikthyr` globally before the test character earns it. The client does not receive it. Then let that character report the boss key through the normal `ZoneSystem.SetGlobalKey` path. The mod records progress, but the client still does not have the key until reconnecting.

`RecordBossProgress` saves the file without requesting a key update. Vanilla `RPC_SetGlobalKey` calls `SendGlobalKeys` only when the global key is new. In contrast, the fresh `defeated_gdking` test was credited and sent immediately.

## Evidence and cleanup

Server evidence starts in `/home/ubuntu/ptb-testing/backups/sst-features-20260909T151715Z`. Client first-pass and ownership-pass logs are saved under `/home/paperspace/sst-validation-*.log`. These files may contain player identifiers and are not committed.

The initial helper setup exposed two probe-only issues: missing unsafe-build settings for publicized assembly access and an invalid negative game timestamp when seeding fermentation. Both were corrected before the corresponding recorded checks. They are not ServerSideTweaks failures.

All eleven tagged fixtures and the four identified test drops were deleted. The Elder trophy was removed and its empty state verified. Both test boss keys were removed, leaving the original `activebosses 2` global key. Both characters were saved and logged out.

The original ServerSideTweaks config and progress-file presence were restored from the config backup. The helper DLL was removed from both plugin folders. Final logs and changed ServerSideTweaks config files were retained under the evidence paths. The temporary client password file was removed. Character saves remain available for later testing. No world rollback was performed; normal world generation caused by travel remains.

The helper's final source also adds an acknowledgement timeout, shares resource-seeding code, and handles inventory stack merging. These final helper refinements were build-checked but were not deployed for another live test. The production mod source is unchanged.

After cleanup, the test server started with its five normal plugins, game port 5900 and RCON port 5902 available, zero players, and only the original global key. No tagged test objects remained. The production server stayed active with its original process ID. Both Release builds completed with zero warnings and errors; `git diff --check` passed.
