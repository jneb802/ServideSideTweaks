# Ownership removal validation

Status: pending. Valdev and Valnet are in use. Do not deploy or start validation until they are available and device leases have been acquired.

## Environment

- Use Valdev and two Valnet clients with development characters.
- Record the original active profiles and modpack versions.
- Create isolated test profiles from the current Season 8 production-mirror profiles. Keep client and server modpack versions aligned.
- Keep NetworkPerformanceSystem (NPS) and Community Patch at the production versions and settings. Record these versions, relevant ownership settings, and the candidate DLL checksum.
- Compare current ServerSideTweaks `main` with the candidate using the same actions and objects.
- Use mmcli-agent for Valdev lifecycle actions. Restore the original profiles and release device leases after collecting evidence.

## Interaction checks

For each action below, have one client own the object or area first and have the other client interact. Repeat after the original owner leaves the area and after that owner disconnects.

| Object | Action | Required result |
| --- | --- | --- |
| Door | Open and close from both clients | Each valid use succeeds and both clients see the same state. |
| Beehive | Extract available honey | Honey is delivered once and both clients see the emptied hive. |
| Sap collector | Extract available sap | Sap is delivered once and both clients see the emptied collector. |
| Fermenter | Add mead base and tap finished mead | Ingredients and output are neither lost nor duplicated; state agrees on both clients. |

Record owners and interaction results. Do not require ownership to move to the interacting player: NPS does not provide equivalent use-triggered transfers for all four object types. If an interaction fails, capture server and client logs and compare with the baseline before changing another mod.

## Boss-stone and routing checks

- Attempt to place a boss trophy on a start-temple boss stone from each client. Confirm the trophy is blocked and neither client sees a persistent trophy visual. The candidate no longer claims server ownership when clearing rejected trophy state.
- Confirm player-built boss stones still accept permitted trophies.
- Confirm boss-location discovery still works.
- Confirm boss messages reach nearby players and remain hidden from distant players.
- Confirm no new routed remote procedure call (RPC) errors appear. Normal object calls must reach the existing Valheim/NPS handling without ServerSideTweaks rewriting their destination.

## Evidence and release condition

Retain baseline and candidate logs, owner observations, screenshots where useful, profile/version records, and DLL checksums. A successful build does not validate networking behavior. Complete the checks above before production deployment.
