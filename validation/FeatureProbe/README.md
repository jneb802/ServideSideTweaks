# Temporary feature probe

This helper is for an isolated Valheim 1.0 test server and test clients. It is excluded from the main mod build. Do not include it in a release or install it on a player-facing server. It exposes test commands that can change object ownership, resource levels, inventory, and boss progress.

Build with `dotnet build validation/FeatureProbe/FeatureProbe.csproj -c Release` from the repository root. Install `bin/Release/ServerSideTweaks.FeatureProbe.dll` from this directory on the test server and client. Use a separate test character and back up the world and relevant configs before running mutation commands.

Client commands run through `valheim-cli sst_probe ...`. Server commands run through RCON `consoleCommand sst_probe ...`; read their results from the server BepInEx log under `SSTPROBE`.

| Command | Purpose |
| --- | --- |
| `status` | Show server/client role and local player state. |
| `views <prefab substring or user:id>` | Inspect matching loaded objects and their owners. |
| `zdo <user:id>` | Inspect a server ZDO. |
| `own <user:id>` | Set a server ZDO's owner to the server. |
| `interact <target>` | Call the object's normal `Interact` method. |
| `item <target> <item prefab>` | Add a test item to inventory and call the normal `UseItem` method. |
| `clearitem <target>` | Remove an attachment placed on an empty stand by this probe session, if the attached item still matches. |
| `damage <target> <chop or pickaxe>` | Send a low-damage hit through `IDestructible.Damage`. |
| `remote interact <target>` | Establish server ownership, then call normal client interaction. |
| `remote item <target> <item prefab>` | Establish server ownership before item use. |
| `remote damage <target> <chop or pickaxe>` | Establish server ownership before a hit. |
| `remote harvest <target>` | Seed three resources and establish server ownership before extraction. |
| `remote tap <target>` | Seed completed minor-health mead and establish server ownership before tapping. |
| `seed <user:id> level <number>` | Seed resource level on the server. |
| `seed <user:id> fermented` | Seed completed minor-health mead on the server. |
| `public <true or false>` | Change the client's requested map-sharing state. |
| `message <boss or control>` | Send a boss or ordinary center-screen message through the routed-message path. |
| `keys` | Show the local global-key set. |
| `bosskey <key>` | Request a global key through the normal game API; this can credit boss progress. |
| `icons` | Show the client's received location icons. |
| `locations <filter>` | Inspect placed server locations. |
| `inventory` | List test inventory contents. |

The `remote` commands use a test-only request and acknowledgement to establish matching server ownership on both peers before the normal game interaction. This exercises the mod's server-to-client transfer path. It does not prove behavior between two player-owned clients or preservation of a hit being handled by a second client.

Use exact ZDO IDs for mutation commands when natural objects share the test prefab's name. Action commands reject targets farther than 30 metres from the player. `remote` command output is asynchronous; inspect subsequent object state and both logs rather than treating its queued response as success.

Cleanup must remove test objects and their drops, restore changed configs and progression, log out the test character, remove this DLL from both plugin folders, and restart the server. Keep evidence and character saves for later review.
