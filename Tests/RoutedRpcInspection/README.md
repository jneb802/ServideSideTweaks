# Routed RPC inspection checks

Run from the repository root with the .NET 10 SDK and the local Valheim assemblies configured in `Environment.props`:

```sh
dotnet run --project Tests/RoutedRpcInspection
dotnet build ServideSideTweaks.csproj --nologo
```

The runner uses the real production incoming patch and dispatcher, plus the local game's `ZRoutedRpc`, `ZDOID`, and `ZPackage` implementations. Feature handlers are replaced with recording collaborators. This isolates header decisions and allocations; it does not validate Unity gameplay, actual feature handlers, Harmony patch ordering, or the complete network receive path.

Runtime assembly resolution defaults to the macOS Steam installation. `VALHEIM_TEST_INSTALL` can override its root; compile-time assembly paths must also agree with `Environment.props`. Game assemblies are not redistributed.

Checks cover:

- Unrelated requests allocate zero bytes in this inspection step after warmup, with boss-message filtering enabled.
- Discovery, debug diagnostics, boss-message filtering, and trophy-block configuration gates.
- Registered handlers receive server-addressed object calls once; other destinations continue to the normal routing path.
- Newly registered handlers, consumed requests, and rewritten destinations.
- Client bypass, preserved input bytes/cursor, truncated headers, and missing payloads.

`--baseline` reports allocations without requiring the optimized behavior. It was used before editing the production patch to record the original behavior. It does not select an embedded old implementation.

## Local result, 2026-09-16

The same loop ran 10,000 requests after 1,000 warmup calls. Test payloads contain a byte array of the stated size, plus its length field. The original patch was from commit `75dccd1` (ServerSideTweaks 1.1.15).

| Byte-array size | Original allocations | Header-filter allocations |
|---|---:|---:|
| 128 bytes | 9,040,000 bytes | 0 bytes |
| 4,096 bytes | 87,200,000 bytes | 0 bytes |

These are .NET 10 local measurements of the additional inspection step. They are not Unity Mono measurements, total server allocations, or proof about production crash causation.

## Required live validation

Live validation is pending. At the implementation attempt, Valdev and the Valnet clients were leased by other tasks. No candidate DLL was deployed and no shared device state was changed.

When the devices are available:

1. Claim Valdev and one Valnet client. Record their previous profiles and verify matching current Season 8 modpack versions. Use isolated copies of those profiles and a test world.
2. With the original 1.1.15 server DLL, join from the client and record routed-request counts and allocation measurements for a fixed unrelated-RPC workload. Use a development character. Record the exact workload so it can be repeated.
3. Stop Valdev through the mmcli-agent API, install the candidate server DLL in the test profile, restart through the API, and repeat the same workload with the same client.
4. Verify ordinary chat/interaction requests, boss-location discovery, boss announcements, and server-addressed door, mining, harvest, pickable, and tree requests. Observe actual feature results and ownership state. A client-owned object alone does not exercise a server-addressed handoff.
5. In the isolated test configuration, exercise trophy blocking both enabled and disabled, fermenter handoff if enabled, and boss-location diagnostics. Confirm there are no duplicate actions and inspect both server and client logs for new warnings/errors.
6. Check compatibility with the production NPS version and record the loaded patch order. Recipient-distance behavior across two players requires a second client if that broader behavior is tested.
7. Restore previous profiles, stop any Valnet machine started for the test, release leases, and attach evidence to the PR. Report measured allocation savings separately from retained-memory behavior and crashes.

This change preserves the existing feature handlers. Moving boss features between mods remains separate work tracked in issue #37.
