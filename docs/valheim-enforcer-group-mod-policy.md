# ValheimEnforcer Group Mod Policy

`serverSideTweaks` can optionally replace ValheimEnforcer's server-side mod-list validation with a group-aware validator.

Enable it in `warpalicious.serverSideTweaks.cfg`:

```ini
[ValheimEnforcer]
EnableGroupModPolicy = true
GroupModPolicyFile = warpalicious.serverSideTweaks.valheimEnforcerGroups.yaml
DebugGroupModPolicy = false
```

`EnableGroupModPolicy` defaults to `true` for new generated configs. Existing config files keep their saved value until edited or regenerated.

Policy file example:

```yaml
groups:
  creative-builders:
    players:
      - "76561198695262471"
    allowedMods:
      com.sinai.unityexplorer:
        pluginID: com.sinai.unityexplorer
        version: 4.12.7
        name: UnityExplorer
        enforceVersion: true
```

Restricted mods should be removed from ValheimEnforcer `requiredMods`, `optionalMods`, and `adminOnlyMods`, then added to this file. If the restricted mod is installed on the server, keep it in ValheimEnforcer `serverOnlyMods` so ValheimEnforcer does not auto-add it back to `requiredMods` on startup.

A connecting player can keep those mods only when their Steam ID is listed in a group that allows them.
