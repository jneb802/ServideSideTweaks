# Live inspection probe

Test-only BepInEx plugin for an isolated Valdev profile and development clients.
Do not package or deploy it to production. Its test RPCs can change object ownership.

Build with `dotnet build Tests/LiveProbe/LiveProbe.csproj -c Release`.
Install the same DLL on the server and clients. Allow its GUID
`praetoris.sst-inspection-probe` in the test server's mod policy.

The server records `BepInEx/sst-inspection-probe.csv` every five seconds.
The counters are cumulative and grouped by incoming RPC method hash.
The probe wraps the real ServerSideTweaks incoming prefix and counts the
`ZPackage` constructors it calls on that thread. It does not replace the prefix.
Package payload bytes are the sum of package sizes at constructor completion,
not total allocated bytes or retained memory.

The allocation counter first checks the managed per-thread API. On the tested
Unity Mono runtime, that API did not work. The probe instead uses Boehm's native
`GC_get_total_bytes`. That counter covers all managed threads in the process.
Its delta during an inspection can include background allocations. Treat these
deltas as approximate attribution; package counts have thread-local attribution.
The probe itself adds work. Do not compare absolute throughput against an
unmonitored production server.

Commands:

- `sst_probe_mark <phase>`: snapshot and mark a server measurement boundary.
- `sst_probe_burst <count> <byteArraySize> <messagesPerFrame>`: send ordinary,
  unrelated routed requests to the server. The receiver checks every payload
  byte and per-client sequence. A new burst starts its sequence at zero.
- `sst_probe_status`: show local progress; receive counts are server-only.
- `sst_probe_arm_safety`: apply god and ghost modes when the development player loads.
- `sst_probe_inspect <prefabPrefix>`: report the nearest object's owner and stored state.
- `sst_probe_interact <prefabPrefix>`: use the nearest object's normal interaction method.
- `sst_probe_damage <prefabPrefix> <amount>`: use its normal destructible damage method.
- `sst_probe_message boss|normal`: exercise announcement routing.
- `sst_probe_discovery`: request the nearest Eikthyr location.
- `sst_probe_teleport <x> <y> <z>`: move the development character for zone tests.
- `sst_probe_owner <prefabPrefix> [UseDoor|RPC_Pick]`: assign the nearest matching
  object to the server, then optionally send a server-addressed interaction.

Use phase names ending in `_start` for measured workloads. Mark a different
phase after the server has received the expected request count. Run
`python3 Tests/LiveProbe/summarize.py <csv>` to summarize those boundaries.
Inspect the five-second memory samples separately when looking for peaks.

A two-client run can prove that this inspection no longer creates redundant
packages for unrelated requests. It cannot establish long-run production memory
growth, 60-player performance, or the cause of a garbage-collector crash.
