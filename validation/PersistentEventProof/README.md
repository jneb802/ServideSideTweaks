# Persistent event validation helper

This is a test-only BepInEx plugin. Do not ship it with the mod or install it on production. Use an isolated world copy and profile. The main project excludes this source.

Build with `dotnet build validation/PersistentEventProof/Proof.csproj -c Release`. Install the resulting `PersistentEventProof.dll` in the test profile's plugins directory.

Write one command into `BepInEx/config/persistent-proof.request`. The helper consumes the file on the Unity main thread and writes `persistent-proof.result` in the same directory.

| Command | Target | Effect |
| --- | --- | --- |
| `info` | Server | Reports definitions and active events. |
| `test` | Server | Runs 100 seeded placement searches and boundary, unloaded-object, disabled-ward, configuration and blocked-search checks. Creates stored test objects. |
| `verify` | Server | Checks active event positions against the current placement rules. |
| `clear` | Server | Stops active events and removes stored objects created by this helper during the current process. |
| `client-start` | Connected client | Sends the game's normal persistent event request. Does not choose or alter the position. |
| `client-info` | Connected client | Reports the replicated event ID, position and radius. |

The server tests change the random seed and feature settings. Successful completion leaves restrictions enabled, `guard_stone` protected and clearance at 100 metres. Discard the test world/profile after use. If a test fails, restore configuration before continuing. `clear` cannot identify test objects after a server restart; this is another reason to use a disposable world copy.

The client helper is necessary on game versions that restrict the built-in event console command to a local server. It does not install the placement feature on the client.
