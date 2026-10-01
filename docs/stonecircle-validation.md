# StoneCircle blocking validation

Validated on 1 October 2026 with Valheim 1.0.16, Valdev, and both Valnet clients.
Production still ran Praetoris Season 8 **8.0.31**. Tests used the maintained
`praetoris-season-8` profiles and a copied season world. Production was not joined or changed.

## Reload fix

The original PR build did not detect edits to its config file on the Linux test
profile. The file contained `true`, but the runtime setting remained `false`.
Calling `Config.Reload()` on the game thread worked, so the global-key rule was
functional and automatic reload was the failed path.

The watcher now checks file content on the game thread once per second. It reloads
only after two checks see the same changed content. It accepts both in-place edits
and atomic file replacement. It retries missing or busy files on a later check.
Reload temporarily disables saving settings to avoid rewriting the file as each
entry loads. The old file watcher and its disposal code were removed.

Live tests changed the watched file without calling `Config.Reload()`. Both edit
methods changed the runtime value. Enabling blocking removed an active key and
synchronized the removal to connected clients.

## Results

| Check | Result |
| --- | --- |
| Blocking disabled; server sets `StoneCircle` | Passed; server and clients received the key |
| Enable blocking with the key active | Passed; server and both clients lost the key |
| Repeated native commands and client requests | Key remained absent |
| Capitalization and `StoneCircle 1` | Blocked |
| Load a save containing the active key with blocking enabled | Key absent after loading |
| Save the filtered world and restart again | Key remained absent |
| Reconnect and load the temple | `Valkyrie_End` was inactive and absent from the active hierarchy |
| Disable blocking after removal | Key remained absent until a new activation |
| Normal temple offering with blocking disabled | FrozenKingDrop attached; temple activated the key |
| Normal temple offering with blocking enabled | FrozenKingDrop attached; key remained absent |
| Unrelated key with a value | `pr47_unrelated 7` synchronized and survived restart |
| Existing per-player filtering | Server defeat keys remained filtered from clients without those personal keys |
| Release build and diff check | Passed; zero build warnings or errors |

The config remains server-only and defaults to:

```ini
[BossStoneTrophies]
BlockStoneCircleGlobalKey = true
```

The full production mod set remained installed. Before applying candidates,
472 server plugin/patcher files and 425 intended client files matched the release
hashes. The changing profiler log was excluded. Clients did not receive the
ServerSideTweaks candidate. PraetorisClient candidates for separate boat and boss
tests, temporary probes, and valheimCLI were recorded test additions. Candidate
and helper hashes were permitted in the test mod policy. External service
credentials were removed from copied configs, and external metric upload was disabled.

The existing trophy-placement restriction was temporarily disabled to exercise the
temple's ordinary attachment path. An early probe used a cloned item reference
after inventory stacking; the corrected probe used the actual inventory item.
The final offering checks confirmed both the attachment and resulting global keys.

An already visible Valkyrie stayed visible until an area reload, as expected.
Disabling blocking did not restore a removed key automatically. No exception came
from the global-key rule or reload fix. Existing headless video/shader errors,
PieceManager preview snapshot errors, asset/audio warnings, and test-helper audit
warnings remained. Temporary probe access errors were corrected before final proof.

Commands, sanitized server/client logs, baseline hashes, and restoration evidence:

`/Users/benjmarston/Develop/valheim-validation-evidence/pr-followup-20261001/`

Test probes and game assemblies are not included in this pull request.
