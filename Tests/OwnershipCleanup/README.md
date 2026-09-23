# Ownership cleanup checks

Run with .NET SDK 8 or later:

```sh
dotnet run --project Tests/OwnershipCleanup/OwnershipCleanup.csproj -c Release
```

The test executable compiles the production cleanup classes against small game stubs. It checks release timing, repeated use, existing ownership, ownership changes, disconnection, deleted objects, world changes, scan timing, the disable setting, and client exclusion. It does not prove Valheim networking behavior or compatibility with the running game's ownership dictionary.

## Required live validation

Use an isolated copy of the current Season 8 production-mirror profiles on Valdev and two clients. Record all profile names and modpack versions. Keep NetworkPerformanceSystem enabled. Compare current `main` with the candidate DLL using the same steps:

1. Have each client use a door, extract from a beehive and sap collector, and add to and tap a fermenter. Verify that actions complete and both clients see the resulting state.
2. Observe ownership on the server. After a handoff, wait for the configured release delay and verify the owner is released if unchanged. Use the object again before the deadline and verify the deadline is extended.
3. Change the owner to the other client. Verify that the old assignment's deadline does not clear the new assignment. Disconnect the current owner and verify prompt release.
4. Destroy a tracked object. Verify that its ownership record is removed. Inspect stale ownership record counts before and after the periodic scan. Verify that existing objects keep their owners.
5. Check client and server logs for new errors. Restore the original profiles and release the device leases.

Live reproduction and candidate validation were blocked on 2026-09-23 because another session held the Valdev lease for external garbage-collection tracing. Historical tests from PR #25 covered removed tree and pickable handlers and are not proof for this replacement.
