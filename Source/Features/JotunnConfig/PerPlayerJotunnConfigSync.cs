using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ServerSideTweaks.Features.JotunnConfig
{
    internal static class PerPlayerJotunnConfigSync
    {
        private const string JotunnConfigRpcId = "com.jotunn.jotunn!ConfigSync";
        private const byte InitialConfigFlag = 64;

        private static readonly Dictionary<long, float> PendingSyncByPeer = new();
        private static readonly Dictionary<string, DateTime> ConfigWriteTimesUtc = new(StringComparer.OrdinalIgnoreCase);
        private static JotunnConfigOverrideFile? _overrideFile;
        private static string? _overridePath;
        private static DateTime _overrideWriteTimeUtc;
        private static bool _missingJotunnLogged;

        internal static void Update()
        {
            if (!IsFeatureEnabled())
            {
                PendingSyncByPeer.Clear();
                return;
            }

            if (!IsServerSyncReady())
            {
                return;
            }

            try
            {
                ReloadOverridesIfNeeded();
                ResendAllIfManagedConfigChanged();
                SendPendingOverrides();
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to update per-player Jotunn config sync: {ex}");
            }
        }

        internal static void SchedulePeerSync(ZNetPeer? peer)
        {
            if (!IsFeatureEnabled() || !IsServerSyncReady() || peer == null)
            {
                return;
            }

            float delay = Mathf.Max(0.0f, ModConfig.JotunnConfigSyncDelaySeconds.Value);
            PendingSyncByPeer[peer.m_uid] = Time.time + delay;
            DebugLog($"Scheduled Jotunn config sync for {DescribePeer(peer)} in {delay:0.##}s.");
        }

        internal static void FilterGeneratedConfigValues(List<Tuple<string, string, string, string>> values)
        {
            if (!IsFeatureEnabled())
            {
                return;
            }

            HashSet<string> managedConfigs = GetOverrideFile().ManagedConfigIdentifiers;
            if (managedConfigs.Count == 0)
            {
                return;
            }

            int before = values.Count;
            values.RemoveAll(entry => managedConfigs.Contains(NormalizeConfigIdentifier(entry.Item1)));
            int removed = before - values.Count;
            if (removed > 0)
            {
                DebugLog($"Removed {removed} managed Jotunn config entr{(removed == 1 ? "y" : "ies")} from default sync.");
            }
        }

        internal static bool FilterIncomingServerConfigPackage(ref ZPackage package)
        {
            if (!IsFeatureEnabled())
            {
                return true;
            }

            HashSet<string> managedConfigs = GetOverrideFile().ManagedConfigIdentifiers;
            if (managedConfigs.Count == 0 || !TryReadConfigPackage(package, out byte flags, out List<ConfigTuple> entries))
            {
                return true;
            }

            int before = entries.Count;
            entries.RemoveAll(entry => managedConfigs.Contains(NormalizeConfigIdentifier(entry.ConfigIdentifier)));
            int removed = before - entries.Count;
            if (removed == 0)
            {
                return true;
            }

            DebugLog($"Blocked {removed} managed Jotunn config entr{(removed == 1 ? "y" : "ies")} from an incoming client config package.");
            if (entries.Count == 0)
            {
                return false;
            }

            package = BuildConfigPackage(flags, entries);
            return true;
        }

        private static void SendPendingOverrides()
        {
            if (ZNet.instance == null)
            {
                PendingSyncByPeer.Clear();
                return;
            }

            if (PendingSyncByPeer.Count == 0)
            {
                return;
            }

            List<long> ready = new();
            foreach (KeyValuePair<long, float> entry in PendingSyncByPeer)
            {
                if (Time.time >= entry.Value)
                {
                    ready.Add(entry.Key);
                }
            }

            foreach (long peerId in ready)
            {
                PendingSyncByPeer.Remove(peerId);
                ZNetPeer peer = ZNet.instance.GetPeer(peerId);
                if (peer != null && peer.IsReady())
                {
                    SendOverrides(peer);
                }
            }
        }

        private static void SendOverrides(ZNetPeer peer)
        {
            List<ConfigTuple> values = BuildConfigValuesForPeer(peer);
            if (values.Count == 0)
            {
                return;
            }

            ZPackage package = BuildConfigPackage(InitialConfigFlag, values);
            if (TrySendWithJotunnConfigRpc(peer.m_uid, package))
            {
                DebugLog($"Sent {values.Count} managed Jotunn config entr{(values.Count == 1 ? "y" : "ies")} to {DescribePeer(peer)}.");
                return;
            }

            if (ZRoutedRpc.instance == null)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Unable to send managed Jotunn config entries to {DescribePeer(peer)} because ZRoutedRpc is not ready.");
                return;
            }

            ZPackage routedPackage = WrapJotunnRpcPackage(package);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, JotunnConfigRpcId, routedPackage);
            DebugLog($"Sent {values.Count} managed Jotunn config entr{(values.Count == 1 ? "y" : "ies")} to {DescribePeer(peer)} without Jotunn CustomRPC fragmentation.");
        }

        private static bool TrySendWithJotunnConfigRpc(long peerId, ZPackage package)
        {
            Type syncType = AccessTools.TypeByName("Jotunn.Managers.SynchronizationManager");
            if (syncType == null)
            {
                LogMissingJotunn();
                return false;
            }

            object? syncManager = AccessTools.Property(syncType, "Instance")?.GetValue(null, null);
            object? configRpc = AccessTools.Field(syncType, "ConfigRPC")?.GetValue(syncManager);
            MethodInfo? sendPackage = configRpc != null
                ? AccessTools.Method(configRpc.GetType(), "SendPackage", new[] { typeof(long), typeof(ZPackage) })
                : null;

            if (configRpc == null || sendPackage == null)
            {
                LogMissingJotunn();
                return false;
            }

            sendPackage.Invoke(configRpc, new object[] { peerId, package });
            return true;
        }

        private static List<ConfigTuple> BuildConfigValuesForPeer(ZNetPeer peer)
        {
            JotunnConfigOverrideFile overrideFile = GetOverrideFile();
            if (overrideFile.ManagedConfigIdentifiers.Count == 0)
            {
                return new List<ConfigTuple>();
            }

            List<ConfigTuple> values = GetServerManagedConfigValues(overrideFile.ManagedConfigIdentifiers);
            if (values.Count == 0)
            {
                return values;
            }

            Dictionary<ConfigEntryKey, int> indexes = BuildConfigIndex(values);
            int applied = 0;
            foreach (KeyValuePair<ConfigEntryKey, string> overrideEntry in overrideFile.GetOverrides(peer.m_playerName))
            {
                if (!TryFindConfigIndex(indexes, overrideEntry.Key, out int index))
                {
                    ServerSideTweaksPlugin.ModLogger.LogWarning(
                        $"Jotunn config override for player '{peer.m_playerName}' references unknown setting '{overrideEntry.Key}'.");
                    continue;
                }

                ConfigTuple existing = values[index];
                values[index] = new ConfigTuple(existing.ConfigIdentifier, existing.Section, existing.Key, overrideEntry.Value);
                applied++;
            }

            DebugLog($"Prepared Jotunn config for {DescribePeer(peer)} with {applied} override(s).");
            return values;
        }

        private static List<ConfigTuple> GetServerManagedConfigValues(HashSet<string> managedConfigIdentifiers)
        {
            List<ConfigTuple> values = new();
            foreach (ConfigFile config in GetJotunnConfigFiles())
            {
                string configIdentifier = GetConfigIdentifier(config);
                if (!managedConfigIdentifiers.Contains(configIdentifier))
                {
                    continue;
                }

                TrackConfigWriteTime(configIdentifier, config.ConfigFilePath);
                foreach (ConfigDefinition definition in config.Keys)
                {
                    ConfigEntryBase entry = config[definition.Section, definition.Key];
                    if (!IsSyncableConfigEntry(entry))
                    {
                        continue;
                    }

                    values.Add(new ConfigTuple(configIdentifier, definition.Section, definition.Key, entry.GetSerializedValue()));
                }
            }

            HashSet<string> foundConfigs = values.Select(value => value.ConfigIdentifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string configIdentifier in managedConfigIdentifiers)
            {
                if (!foundConfigs.Contains(configIdentifier))
                {
                    ServerSideTweaksPlugin.ModLogger.LogWarning($"Managed Jotunn config '{configIdentifier}' was not found or has no syncable entries.");
                }
            }

            return values;
        }

        private static IEnumerable<ConfigFile> GetJotunnConfigFiles()
        {
            Type syncType = AccessTools.TypeByName("Jotunn.Managers.SynchronizationManager");
            object? syncManager = syncType != null
                ? AccessTools.Property(syncType, "Instance")?.GetValue(null, null)
                : null;
            MethodInfo? getConfigFiles = syncType != null ? AccessTools.Method(syncType, "GetConfigFiles") : null;

            if (syncManager != null && getConfigFiles != null)
            {
                object? result = getConfigFiles.Invoke(syncManager, Array.Empty<object>());
                if (result is IEnumerable<ConfigFile> configs)
                {
                    return configs;
                }
            }

            return Chainloader.PluginInfos.Values.Select(info => info.Instance.Config);
        }

        private static bool IsSyncableConfigEntry(ConfigEntryBase entry)
        {
            foreach (object tag in entry.Description.Tags)
            {
                Type type = tag.GetType();
                if (type.Name != "ConfigurationManagerAttributes")
                {
                    continue;
                }

                PropertyInfo? property = type.GetProperty("IsAdminOnly");
                if (property != null && property.GetValue(tag, null) is bool propertyValue)
                {
                    return propertyValue;
                }

                FieldInfo? field = type.GetField("IsAdminOnly");
                if (field != null && field.GetValue(tag) is bool fieldValue)
                {
                    return fieldValue;
                }
            }

            return false;
        }

        private static Dictionary<ConfigEntryKey, int> BuildConfigIndex(List<ConfigTuple> values)
        {
            Dictionary<ConfigEntryKey, int> indexes = new();
            for (int i = 0; i < values.Count; i++)
            {
                ConfigTuple value = values[i];
                ConfigEntryKey exact = new(value.ConfigIdentifier, value.Section, value.Key);
                indexes[exact] = i;

                string relaxedSection = RelaxSectionName(value.Section);
                if (!string.Equals(relaxedSection, value.Section, StringComparison.OrdinalIgnoreCase))
                {
                    indexes[new ConfigEntryKey(value.ConfigIdentifier, relaxedSection, value.Key)] = i;
                }
            }

            return indexes;
        }

        private static bool TryFindConfigIndex(Dictionary<ConfigEntryKey, int> indexes, ConfigEntryKey key, out int index)
        {
            return indexes.TryGetValue(key, out index) ||
                indexes.TryGetValue(new ConfigEntryKey(key.ConfigIdentifier, RelaxSectionName(key.Section), key.Key), out index);
        }

        private static string RelaxSectionName(string section)
        {
            int separator = section.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                return section;
            }

            string prefix = section.Substring(0, separator).Trim();
            return prefix.All(char.IsDigit) ? section.Substring(separator + 3).Trim() : section;
        }

        private static bool TryReadConfigPackage(ZPackage package, out byte flags, out List<ConfigTuple> entries)
        {
            flags = 0;
            entries = new List<ConfigTuple>();

            try
            {
                package.SetPos(0);
                flags = package.ReadByte();
                int count = package.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    entries.Add(new ConfigTuple(
                        package.ReadString(),
                        package.ReadString(),
                        package.ReadString(),
                        package.ReadString()));
                }

                package.SetPos(0);
                return true;
            }
            catch (Exception ex)
            {
                package.SetPos(0);
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to parse Jotunn config package: {ex.Message}");
                return false;
            }
        }

        private static ZPackage BuildConfigPackage(byte flags, List<ConfigTuple> entries)
        {
            ZPackage package = new();
            package.Write(flags);
            package.Write(entries.Count);
            foreach (ConfigTuple entry in entries)
            {
                package.Write(entry.ConfigIdentifier);
                package.Write(entry.Section);
                package.Write(entry.Key);
                package.Write(entry.Value);
            }

            return package;
        }

        private static ZPackage WrapJotunnRpcPackage(ZPackage package)
        {
            ZPackage routedPackage = new();
            routedPackage.Write((byte)1);
            routedPackage.Write(package.GetArray());
            routedPackage.SetPos(0);
            return routedPackage;
        }

        private static void ReloadOverridesIfNeeded()
        {
            string path = GetOverridePath();
            EnsureSampleOverrideFile(path);

            DateTime writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_overrideFile != null &&
                string.Equals(_overridePath, path, StringComparison.Ordinal) &&
                writeTime == _overrideWriteTimeUtc)
            {
                return;
            }

            _overrideFile = JotunnConfigOverrideFile.Load(path);
            _overridePath = path;
            _overrideWriteTimeUtc = writeTime;
            ConfigWriteTimesUtc.Clear();
            DebugLog($"Loaded per-player Jotunn config overrides from {path}.");
            ScheduleAllConnectedPeers();
        }

        private static string GetOverridePath()
        {
            string configured = ModConfig.JotunnConfigOverridesFile.Value.Trim();
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Paths.ConfigPath, configured);
        }

        private static void EnsureSampleOverrideFile(string path)
        {
            if (File.Exists(path))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Paths.ConfigPath);
            File.WriteAllLines(path, new[]
            {
                "# serverSideTweaks per-player Jotunn config overrides",
                "# Only this YAML subset is supported: mappings indented by two spaces, scalar values only.",
                "# Config names are Jotunn config identifiers, usually the config filename under BepInEx/config.",
                "players:",
                "  Ben:",
                "    Searica.Valheim.MoreVanillaBuildPrefabs.cfg:",
                "      Global:",
                "        CreativeMode: true",
                "      ArmorStand_Female:",
                "        Enabled: true"
            });
        }

        private static JotunnConfigOverrideFile GetOverrideFile()
        {
            ReloadOverridesIfNeeded();
            return _overrideFile ?? JotunnConfigOverrideFile.Empty;
        }

        private static void ResendAllIfManagedConfigChanged()
        {
            JotunnConfigOverrideFile overrideFile = GetOverrideFile();
            if (overrideFile.ManagedConfigIdentifiers.Count == 0)
            {
                return;
            }

            bool changed = false;
            foreach (ConfigFile config in GetJotunnConfigFiles())
            {
                string configIdentifier = GetConfigIdentifier(config);
                if (!overrideFile.ManagedConfigIdentifiers.Contains(configIdentifier))
                {
                    continue;
                }

                changed |= TrackConfigWriteTime(configIdentifier, config.ConfigFilePath);
            }

            if (changed)
            {
                DebugLog("Detected managed Jotunn config file change; scheduling sync for connected peers.");
                ScheduleAllConnectedPeers();
            }
        }

        private static bool TrackConfigWriteTime(string configIdentifier, string path)
        {
            DateTime writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (ConfigWriteTimesUtc.TryGetValue(configIdentifier, out DateTime previous) && previous == writeTime)
            {
                return false;
            }

            ConfigWriteTimesUtc[configIdentifier] = writeTime;
            return previous != default;
        }

        private static void ScheduleAllConnectedPeers()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer != null && peer.IsReady())
                {
                    SchedulePeerSync(peer);
                }
            }
        }

        private static string GetConfigIdentifier(ConfigFile config)
        {
            string relative = config.ConfigFilePath.Replace(Paths.ConfigPath, "").Replace("\\", "/").Trim('/');
            return NormalizeConfigIdentifier(relative);
        }

        private static string NormalizeConfigIdentifier(string value)
        {
            return value.Trim().Replace("\\", "/").Trim('/');
        }

        private static bool IsFeatureEnabled()
        {
            return ModConfig.EnablePerPlayerJotunnConfigSync.Value == true;
        }

        private static bool IsServerSyncReady()
        {
            return ZNet.instance != null && ZNet.instance.IsServer() && ZRoutedRpc.instance != null;
        }

        private static void LogMissingJotunn()
        {
            if (_missingJotunnLogged)
            {
                return;
            }

            _missingJotunnLogged = true;
            ServerSideTweaksPlugin.ModLogger.LogWarning("Per-player Jotunn config sync is enabled, but Jotunn was not found.");
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugPerPlayerJotunnConfigSync.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private static string DescribePeer(ZNetPeer peer)
        {
            return $"{peer.m_playerName} peer={peer.m_uid} host={peer.m_socket?.GetHostName()}";
        }

        private readonly struct ConfigTuple
        {
            internal ConfigTuple(string configIdentifier, string section, string key, string value)
            {
                ConfigIdentifier = NormalizeConfigIdentifier(configIdentifier);
                Section = section;
                Key = key;
                Value = value;
            }

            internal string ConfigIdentifier { get; }
            internal string Section { get; }
            internal string Key { get; }
            internal string Value { get; }
        }

        private readonly struct ConfigEntryKey : IEquatable<ConfigEntryKey>
        {
            internal ConfigEntryKey(string configIdentifier, string section, string key)
            {
                ConfigIdentifier = NormalizeConfigIdentifier(configIdentifier);
                Section = section.Trim();
                Key = key.Trim();
            }

            internal string ConfigIdentifier { get; }
            internal string Section { get; }
            internal string Key { get; }

            public bool Equals(ConfigEntryKey other)
            {
                return string.Equals(ConfigIdentifier, other.ConfigIdentifier, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Section, other.Section, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object? obj)
            {
                return obj is ConfigEntryKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(ConfigIdentifier);
                hash = hash * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Section);
                hash = hash * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Key);
                return hash;
            }

            public override string ToString()
            {
                return $"{ConfigIdentifier}.{Section}.{Key}";
            }
        }

        private sealed class JotunnConfigOverrideFile
        {
            internal static readonly JotunnConfigOverrideFile Empty = new();

            private readonly Dictionary<string, Dictionary<ConfigEntryKey, string>> _overridesByPlayer = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<ConfigEntryKey, string> _defaultOverrides = new();

            internal HashSet<string> ManagedConfigIdentifiers { get; } = new(StringComparer.OrdinalIgnoreCase);

            internal static JotunnConfigOverrideFile Load(string path)
            {
                JotunnConfigOverrideFile file = new();
                if (!File.Exists(path))
                {
                    return file;
                }

                string context = "";
                string playerName = "";
                string configIdentifier = "";
                string section = "";
                int lineNumber = 0;

                foreach (string rawLine in File.ReadAllLines(path))
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(rawLine) || rawLine.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int indent = CountIndent(rawLine);
                    string trimmed = rawLine.Trim();
                    int colon = trimmed.IndexOf(':');
                    if (colon < 0)
                    {
                        Warn(path, lineNumber, "line is not a YAML mapping");
                        continue;
                    }

                    string key = Unquote(trimmed.Substring(0, colon).Trim());
                    string value = trimmed.Substring(colon + 1).Trim();

                    if (indent == 0)
                    {
                        context = key.Equals("players", StringComparison.OrdinalIgnoreCase) ? "players" :
                            key.Equals("default", StringComparison.OrdinalIgnoreCase) ? "default" : "";
                        if (context.Length == 0)
                        {
                            Warn(path, lineNumber, "top-level key must be 'players' or 'default'");
                        }
                        playerName = "";
                        configIdentifier = "";
                        section = "";
                        continue;
                    }

                    if (context == "players")
                    {
                        if (indent == 2)
                        {
                            playerName = key;
                            configIdentifier = "";
                            section = "";
                        }
                        else if (indent == 4)
                        {
                            configIdentifier = NormalizeConfigIdentifier(key);
                            file.ManagedConfigIdentifiers.Add(configIdentifier);
                            section = "";
                        }
                        else if (indent == 6)
                        {
                            section = key;
                        }
                        else if (indent == 8)
                        {
                            if (string.IsNullOrWhiteSpace(playerName) ||
                                string.IsNullOrWhiteSpace(configIdentifier) ||
                                string.IsNullOrWhiteSpace(section))
                            {
                                Warn(path, lineNumber, "setting is missing player, config, or section context");
                                continue;
                            }

                            file.SetPlayerOverride(playerName, new ConfigEntryKey(configIdentifier, section, key), ParseScalar(value));
                        }
                        else
                        {
                            Warn(path, lineNumber, "unsupported indentation under players");
                        }
                    }
                    else if (context == "default")
                    {
                        if (indent == 2)
                        {
                            configIdentifier = NormalizeConfigIdentifier(key);
                            file.ManagedConfigIdentifiers.Add(configIdentifier);
                            section = "";
                        }
                        else if (indent == 4)
                        {
                            section = key;
                        }
                        else if (indent == 6)
                        {
                            if (string.IsNullOrWhiteSpace(configIdentifier) || string.IsNullOrWhiteSpace(section))
                            {
                                Warn(path, lineNumber, "default setting is missing config or section context");
                                continue;
                            }

                            file.SetDefaultOverride(new ConfigEntryKey(configIdentifier, section, key), ParseScalar(value));
                        }
                        else
                        {
                            Warn(path, lineNumber, "unsupported indentation under default");
                        }
                    }
                    else
                    {
                        Warn(path, lineNumber, "line is outside a supported top-level key");
                    }
                }

                return file;
            }

            internal IEnumerable<KeyValuePair<ConfigEntryKey, string>> GetOverrides(string playerName)
            {
                Dictionary<ConfigEntryKey, string> merged = new(_defaultOverrides);
                if (_overridesByPlayer.TryGetValue(playerName ?? "", out Dictionary<ConfigEntryKey, string> playerOverrides))
                {
                    foreach (KeyValuePair<ConfigEntryKey, string> entry in playerOverrides)
                    {
                        merged[entry.Key] = entry.Value;
                    }
                }

                return merged;
            }

            private void SetDefaultOverride(ConfigEntryKey key, string value)
            {
                ManagedConfigIdentifiers.Add(key.ConfigIdentifier);
                _defaultOverrides[key] = value;
            }

            private void SetPlayerOverride(string playerName, ConfigEntryKey key, string value)
            {
                ManagedConfigIdentifiers.Add(key.ConfigIdentifier);
                if (!_overridesByPlayer.TryGetValue(playerName, out Dictionary<ConfigEntryKey, string> overrides))
                {
                    overrides = new Dictionary<ConfigEntryKey, string>();
                    _overridesByPlayer[playerName] = overrides;
                }

                overrides[key] = value;
            }

            private static int CountIndent(string line)
            {
                int count = 0;
                foreach (char c in line)
                {
                    if (c == ' ')
                    {
                        count++;
                        continue;
                    }

                    if (c == '\t')
                    {
                        count += 2;
                        continue;
                    }

                    break;
                }

                return count;
            }

            private static string ParseScalar(string value)
            {
                if (value.Length == 0)
                {
                    return "";
                }

                return Unquote(value.Trim());
            }

            private static string Unquote(string value)
            {
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[value.Length - 1] == '"') ||
                     (value[0] == '\'' && value[value.Length - 1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                return value.Replace("\\\"", "\"").Replace("\\'", "'");
            }

            private static void Warn(string path, int lineNumber, string message)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"Invalid Jotunn config override YAML at {path}:{lineNumber}: {message}.");
            }
        }
    }

    [HarmonyPatch]
    internal static class JotunnGenerateConfigZPackagePatch
    {
        private static bool Prepare()
        {
            return AccessTools.TypeByName("Jotunn.Managers.SynchronizationManager") != null;
        }

        private static MethodBase? TargetMethod()
        {
            Type? type = AccessTools.TypeByName("Jotunn.Managers.SynchronizationManager");
            return type == null
                ? null
                : AccessTools.Method(type, "GenerateConfigZPackage", new[] { typeof(bool), typeof(List<Tuple<string, string, string, string>>) });
        }

        private static void Prefix(List<Tuple<string, string, string, string>> values)
        {
            PerPlayerJotunnConfigSync.FilterGeneratedConfigValues(values);
        }
    }

    [HarmonyPatch]
    internal static class JotunnConfigRpcOnServerReceivePatch
    {
        private static bool Prepare()
        {
            return AccessTools.TypeByName("Jotunn.Managers.SynchronizationManager") != null;
        }

        private static MethodBase? TargetMethod()
        {
            Type? type = AccessTools.TypeByName("Jotunn.Managers.SynchronizationManager");
            return type == null
                ? null
                : AccessTools.Method(type, "ConfigRPC_OnServerReceive", new[] { typeof(long), typeof(ZPackage) });
        }

        private static bool Prefix(ref ZPackage package)
        {
            return PerPlayerJotunnConfigSync.FilterIncomingServerConfigPackage(ref package);
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
    internal static class ZNetRpcCharacterIdJotunnConfigSyncPatch
    {
        private static void Postfix(ZNet __instance, ZRpc rpc)
        {
            foreach (ZNetPeer peer in __instance.GetPeers())
            {
                if (peer != null && peer.m_rpc == rpc)
                {
                    PerPlayerJotunnConfigSync.SchedulePeerSync(peer);
                    return;
                }
            }
        }
    }
}
