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

## Live validation

The 2026-09-16 Valdev run used both Valnet clients, a production-copy world, and
verified Season 8 8.0.19 DLLs. The actual Unity server inspection created 28,000
extra packages for 14,000 baseline requests and zero for the same candidate
workload. All measured requests arrived with valid payloads and sequences.

See [the live report](../LiveProbe/RESULTS-2026-09-16.md) for native allocation
measurements, memory/GC samples, gameplay coverage, setup failures, and remaining
risks. The client travel sequence was incomplete. This is not proof that the
production garbage-collector crashes are fixed.

This change preserves the existing feature handlers. Moving boss features between mods remains separate work tracked in issue #37.
