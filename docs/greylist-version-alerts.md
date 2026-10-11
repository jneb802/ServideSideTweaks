# Greylist version alerts

ValheimEnforcer must enforce the version before ServerSideTweaks can report a
greylist version rejection. In the server's `ValheimEnforcer/Mods.yaml`, set
`enforceVersion: true` on each `optionalMods` entry that has a supported version:

```yaml
optionalMods:
  shudnal.MyLittleUI:
    pluginID: shudnal.MyLittleUI
    name: My Little UI
    version: 1.2.29
    enforceVersion: true
```

Keep existing hashes and other entry fields. Leave entries without a version
unchanged. The mod remains optional: a client without it can join. A client with
it installed must use the configured version. This works with
`HashEnforcement = Off`; it does not require file hash checking or an admin
exemption change.

ServerSideTweaks sends these failures in `greylist_version_mismatches`. The
Praetoris bot displays them under **Change these greylist mods**, separate from
**Change these mods**. Missing required mods and unapproved mods keep their
existing sections. A greylist-only failure produces an alert and participates in
the same duplicate suppression as other failures.

Deploy the bot's matching message-format update before this server DLL. Older
bot versions do not display the new field. Apply the policy change with the
normal server configuration workflow. The DLL change alone does not override
the server's version policy.
Keep the live and staged release policies aligned so the next release preserves
the greylist version rules.

This alert path handles server mod validation. A disconnect initiated only by a
client mod is not reported through this path.
