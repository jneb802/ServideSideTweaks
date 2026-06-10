using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ServerSideTweaks.Features.ValheimEnforcer
{
    internal static class ValheimEnforcerGroupModPolicy
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        private static bool _patched;
        private static DateTime _lastWriteTimeUtc;
        private static GroupPolicyFile _policy = new();

        internal static void TryPatch(Harmony harmony)
        {
            if (_patched)
            {
                return;
            }

            MethodInfo? target = TargetMethod();
            if (target == null)
            {
                DebugLog("ValheimEnforcer ModManager RPC not found. Group mod policy is inactive.");
                return;
            }

            MethodInfo prefix = AccessTools.Method(typeof(ValheimEnforcerGroupModPolicy), nameof(Prefix));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix, Priority.First));
            _patched = true;
            ServerSideTweaksPlugin.ModLogger.LogInfo("Patched ValheimEnforcer mod validation for group mod policy.");
        }

        private static MethodInfo? TargetMethod()
        {
            Type? modManager = AccessTools.TypeByName("ValheimEnforcer.modules.ModManager");
            return modManager == null
                ? null
                : AccessTools.Method(modManager, "RPC_ReceiveModVersionData");
        }

        private static bool Prefix(ZRpc sender, ZPackage data)
        {
            if (!ModConfig.EnableValheimEnforcerGroupModPolicy.Value ||
                ZNet.instance == null ||
                !ZNet.instance.IsServer() ||
                sender == null ||
                data == null)
            {
                return true;
            }

            try
            {
                ValidationContext? context = BuildValidationContext(sender, data);
                if (context == null)
                {
                    return true;
                }

                ValidationResult result = Validate(context);
                if (result.IsValid)
                {
                    DebugLog("Accepted " + context.DescribePlayer() + " with groups [" + string.Join(", ", context.Groups) + "].");
                }
                else
                {
                    ServerSideTweaksPlugin.ModLogger.LogWarning(
                        "ValheimEnforcer group mod policy rejected " + context.DescribePlayer() + ": " + result.Summary);
                    sender.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                }

                return false;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning(
                    "ValheimEnforcer group mod policy failed; falling back to ValheimEnforcer validation: " + ex);
                return true;
            }
        }

        private static ValidationContext? BuildValidationContext(ZRpc sender, ZPackage data)
        {
            Type? modsType = AccessTools.TypeByName("ValheimEnforcer.common.DataObjects+Mods");
            Type? modManager = AccessTools.TypeByName("ValheimEnforcer.modules.ModManager");
            if (modsType == null || modManager == null)
            {
                return null;
            }

            object? clientMods = Activator.CreateInstance(modsType);
            MethodInfo? fromZPackage = AccessTools.Method(modsType, "FromZPackage");
            PropertyInfo? modSettingsProperty = AccessTools.Property(modManager, "ModSettings");
            object? serverMods = modSettingsProperty?.GetValue(null, null);
            if (clientMods == null || fromZPackage == null || serverMods == null)
            {
                return null;
            }

            ZPackage packageCopy = new(data.GetArray());
            clientMods = fromZPackage.Invoke(clientMods, new object[] { packageCopy });
            if (clientMods == null)
            {
                return null;
            }

            ZNetPeer? peer = FindPeer(sender);
            string playerId = StablePlayerId(peer, sender);
            string playerName = peer?.m_playerName ?? "";
            string endpoint = SafeEndPoint(peer, sender);
            bool isAdmin = !string.IsNullOrWhiteSpace(playerId) && ZNet.instance.IsAdmin(playerId);
            GroupPolicyFile policy = LoadPolicy();
            IReadOnlyList<string> groups = policy.GetGroups(playerId, playerName);

            return new ValidationContext(
                playerId,
                playerName,
                endpoint,
                isAdmin,
                groups,
                GetMods(clientMods, "ActiveMods"),
                GetMods(serverMods, "RequiredMods"),
                GetMods(serverMods, "OptionalMods"),
                GetMods(serverMods, "AdminOnlyMods"),
                policy.GetAllowedMods(groups));
        }

        private static ValidationResult Validate(ValidationContext context)
        {
            List<ModIssue> missing = new();
            List<ModIssue> mismatches = new();
            List<ModIssue> extras = new();

            foreach (ModIssue requiredMod in context.RequiredMods.Values)
            {
                if (!context.ActiveMods.ContainsKey(requiredMod.Key))
                {
                    missing.Add(requiredMod.WithActual(""));
                }
            }

            foreach (ModIssue clientMod in context.ActiveMods.Values)
            {
                if (TryAddVersionMismatch(context.RequiredMods, clientMod, mismatches, enforceForThisPlayer: true))
                {
                    continue;
                }

                if (context.AdminOnlyMods.TryGetValue(clientMod.Key, out ModIssue adminMod))
                {
                    if (context.IsAdmin)
                    {
                        AddMismatchIfNeeded(mismatches, adminMod, clientMod, enforceForThisPlayer: true);
                        continue;
                    }
                }

                if (TryAddVersionMismatch(context.OptionalMods, clientMod, mismatches, enforceForThisPlayer: true))
                {
                    continue;
                }

                if (TryAddVersionMismatch(context.GroupAllowedMods, clientMod, mismatches, enforceForThisPlayer: true))
                {
                    continue;
                }

                extras.Add(clientMod);
            }

            return new ValidationResult(missing, mismatches, extras);
        }

        private static bool TryAddVersionMismatch(
            ModDictionary allowedMods,
            ModIssue clientMod,
            List<ModIssue> mismatches,
            bool enforceForThisPlayer)
        {
            if (!allowedMods.TryGetValue(clientMod.Key, out ModIssue allowedMod))
            {
                return false;
            }

            AddMismatchIfNeeded(mismatches, allowedMod, clientMod, enforceForThisPlayer);
            return true;
        }

        private static void AddMismatchIfNeeded(
            List<ModIssue> mismatches,
            ModIssue serverMod,
            ModIssue clientMod,
            bool enforceForThisPlayer)
        {
            if (!enforceForThisPlayer || !serverMod.EnforceVersion)
            {
                return;
            }

            if (!string.Equals(serverMod.ExpectedVersion, clientMod.ActualVersion, StringComparison.Ordinal))
            {
                mismatches.Add(serverMod.WithActual(clientMod.ActualVersion));
            }
        }

        private static GroupPolicyFile LoadPolicy()
        {
            string path = GetPolicyPath();
            EnsurePolicyFile(path);

            DateTime writeTimeUtc = File.GetLastWriteTimeUtc(path);
            if (writeTimeUtc == _lastWriteTimeUtc)
            {
                return _policy;
            }

            try
            {
                string yaml = File.ReadAllText(path);
                _policy = Deserializer.Deserialize<GroupPolicyFile>(yaml) ?? new GroupPolicyFile();
                _policy.Normalize();
                _lastWriteTimeUtc = writeTimeUtc;
                ServerSideTweaksPlugin.ModLogger.LogInfo(
                    "Loaded ValheimEnforcer group mod policy from " + path + ": groups=" + _policy.Groups.Count + ".");
            }
            catch (Exception ex)
            {
                _lastWriteTimeUtc = writeTimeUtc;
                ServerSideTweaksPlugin.ModLogger.LogWarning(
                    "Failed to load ValheimEnforcer group mod policy from " + path + ": " + ex.Message);
            }

            return _policy;
        }

        private static void EnsurePolicyFile(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(path))
            {
                return;
            }

            File.WriteAllText(path,
                "# Mods listed here are allowed only for players in matching groups.\n" +
                "# Put restricted mods in this file instead of ValheimEnforcer optionalMods/adminOnlyMods.\n" +
                "groups: {}\n");
        }

        private static string GetPolicyPath()
        {
            string value = ModConfig.ValheimEnforcerGroupModPolicyFile.Value.Trim();
            string fileName = string.IsNullOrWhiteSpace(value)
                ? "warpalicious.serverSideTweaks.valheimEnforcerGroups.yaml"
                : value;
            return Path.IsPathRooted(fileName) ? fileName : Path.Combine(Paths.ConfigPath, fileName);
        }

        private static ModDictionary GetMods(object mods, string propertyName)
        {
            ModDictionary result = new();
            object? dictionary = AccessTools.Property(mods.GetType(), propertyName)?.GetValue(mods, null);
            if (dictionary is not IDictionary rawDictionary)
            {
                return result;
            }

            foreach (DictionaryEntry entry in rawDictionary)
            {
                string key = entry.Key?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(key) || entry.Value == null)
                {
                    continue;
                }

                ModIssue issue = ModIssue.FromMod(key, entry.Value);
                result[key] = issue;
            }

            return result;
        }

        private static ZNetPeer? FindPeer(ZRpc sender)
        {
            try
            {
                return ZNet.instance.GetConnectedPeers()
                    .FirstOrDefault(peer => ReferenceEquals(peer.m_rpc, sender) ||
                                            ReferenceEquals(peer.m_socket, sender.GetSocket()));
            }
            catch
            {
                return null;
            }
        }

        private static string StablePlayerId(ZNetPeer? peer, ZRpc sender)
        {
            try
            {
                string hostName = peer?.m_socket?.GetHostName() ?? sender.GetSocket()?.GetHostName() ?? "";
                return string.IsNullOrWhiteSpace(hostName) ? peer?.m_uid.ToString() ?? "" : hostName;
            }
            catch
            {
                return peer?.m_uid.ToString() ?? "";
            }
        }

        private static string SafeEndPoint(ZNetPeer? peer, ZRpc sender)
        {
            try
            {
                return peer?.m_socket?.GetEndPointString() ?? sender.GetSocket()?.GetEndPointString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string GetStringProperty(object target, string name)
        {
            return AccessTools.Property(target.GetType(), name)?.GetValue(target, null)?.ToString() ?? "";
        }

        private static bool GetBoolProperty(object target, string name)
        {
            object? value = AccessTools.Property(target.GetType(), name)?.GetValue(target, null);
            return value is bool boolValue && boolValue;
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugValheimEnforcerGroupModPolicy.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo("[ValheimEnforcerGroupModPolicy] " + message);
            }
        }

        private sealed class GroupPolicyFile
        {
            public Dictionary<string, GroupPolicy> Groups { get; set; } = new();

            public void Normalize()
            {
                Groups ??= new Dictionary<string, GroupPolicy>();
                foreach (KeyValuePair<string, GroupPolicy> group in Groups.ToList())
                {
                    if (string.IsNullOrWhiteSpace(group.Key) || group.Value == null)
                    {
                        Groups.Remove(group.Key);
                        continue;
                    }

                    group.Value.Normalize();
                }
            }

            public IReadOnlyList<string> GetGroups(string playerId, string playerName)
            {
                List<string> groups = new();
                foreach (KeyValuePair<string, GroupPolicy> entry in Groups)
                {
                    if (entry.Value.ContainsPlayer(playerId, playerName))
                    {
                        groups.Add(entry.Key);
                    }
                }

                return groups;
            }

            public ModDictionary GetAllowedMods(IReadOnlyList<string> groupNames)
            {
                ModDictionary result = new();
                foreach (string groupName in groupNames)
                {
                    if (!Groups.TryGetValue(groupName, out GroupPolicy group))
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, ModIssue> mod in group.ModRecords)
                    {
                        result[mod.Key] = mod.Value;
                    }
                }

                return result;
            }
        }

        private sealed class GroupPolicy
        {
            public List<string> Players { get; set; } = new();
            public List<string> Users { get; set; } = new();
            public List<string> PlayerIds { get; set; } = new();
            public List<string> Names { get; set; } = new();
            public Dictionary<string, PolicyMod> Mods { get; set; } = new();
            public Dictionary<string, PolicyMod> AllowedMods { get; set; } = new();
            public ModDictionary ModRecords { get; } = new();

            public void Normalize()
            {
                Players = NormalizeList(Players);
                Users = NormalizeList(Users);
                PlayerIds = NormalizeList(PlayerIds);
                Names = NormalizeList(Names);
                Mods ??= new Dictionary<string, PolicyMod>();
                AllowedMods ??= new Dictionary<string, PolicyMod>();
                ModRecords.Clear();
                AddMods(Mods);
                AddMods(AllowedMods);
            }

            public bool ContainsPlayer(string playerId, string playerName)
            {
                return Contains(Players, playerId) ||
                       Contains(Users, playerId) ||
                       Contains(PlayerIds, playerId) ||
                       Contains(Names, playerName);
            }

            private void AddMods(Dictionary<string, PolicyMod> mods)
            {
                foreach (KeyValuePair<string, PolicyMod> entry in mods)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value == null)
                    {
                        continue;
                    }

                    entry.Value.Normalize(entry.Key);
                    ModRecords[entry.Key] = ModIssue.FromPolicyMod(entry.Key, entry.Value);
                }
            }

            private static List<string> NormalizeList(List<string>? values)
            {
                return values?
                    .Select(value => value?.Trim() ?? "")
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
            }

            private static bool Contains(List<string> values, string target)
            {
                return !string.IsNullOrWhiteSpace(target) &&
                       values.Any(value => string.Equals(value, target, StringComparison.OrdinalIgnoreCase));
            }
        }

        private sealed class PolicyMod
        {
            public string PluginID { get; set; } = "";
            public string Version { get; set; } = "";
            public string Name { get; set; } = "";
            public bool EnforceVersion { get; set; }

            public void Normalize(string fallbackKey)
            {
                PluginID = string.IsNullOrWhiteSpace(PluginID) ? fallbackKey : PluginID.Trim();
                Version = Version?.Trim() ?? "";
                Name = string.IsNullOrWhiteSpace(Name) ? PluginID : Name.Trim();
            }
        }

        private sealed class ValidationContext
        {
            public string PlayerId { get; }
            public string PlayerName { get; }
            public string Endpoint { get; }
            public bool IsAdmin { get; }
            public IReadOnlyList<string> Groups { get; }
            public ModDictionary ActiveMods { get; }
            public ModDictionary RequiredMods { get; }
            public ModDictionary OptionalMods { get; }
            public ModDictionary AdminOnlyMods { get; }
            public ModDictionary GroupAllowedMods { get; }

            public ValidationContext(
                string playerId,
                string playerName,
                string endpoint,
                bool isAdmin,
                IReadOnlyList<string> groups,
                ModDictionary activeMods,
                ModDictionary requiredMods,
                ModDictionary optionalMods,
                ModDictionary adminOnlyMods,
                ModDictionary groupAllowedMods)
            {
                PlayerId = playerId;
                PlayerName = playerName;
                Endpoint = endpoint;
                IsAdmin = isAdmin;
                Groups = groups;
                ActiveMods = activeMods;
                RequiredMods = requiredMods;
                OptionalMods = optionalMods;
                AdminOnlyMods = adminOnlyMods;
                GroupAllowedMods = groupAllowedMods;
            }

            public string DescribePlayer()
            {
                if (!string.IsNullOrWhiteSpace(PlayerName) && !string.IsNullOrWhiteSpace(PlayerId))
                {
                    return PlayerName + " (`" + PlayerId + "`) endpoint=" + Endpoint;
                }

                return (string.IsNullOrWhiteSpace(PlayerId) ? "unknown" : "`" + PlayerId + "`") + " endpoint=" + Endpoint;
            }
        }

        private sealed class ValidationResult
        {
            private IReadOnlyList<ModIssue> MissingRequiredMods { get; }
            private IReadOnlyList<ModIssue> VersionMismatches { get; }
            private IReadOnlyList<ModIssue> ExtraMods { get; }

            public ValidationResult(
                IReadOnlyList<ModIssue> missingRequiredMods,
                IReadOnlyList<ModIssue> versionMismatches,
                IReadOnlyList<ModIssue> extraMods)
            {
                MissingRequiredMods = missingRequiredMods;
                VersionMismatches = versionMismatches;
                ExtraMods = extraMods;
            }

            public bool IsValid => MissingRequiredMods.Count == 0 &&
                                   VersionMismatches.Count == 0 &&
                                   ExtraMods.Count == 0;

            public string Summary =>
                MissingRequiredMods.Count + " missing, " +
                VersionMismatches.Count + " version mismatch, " +
                ExtraMods.Count + " extra";
        }

        private sealed class ModDictionary : Dictionary<string, ModIssue>
        {
        }

        private sealed class ModIssue
        {
            public string Key { get; }
            public string ExpectedVersion { get; }
            public string ActualVersion { get; }
            public bool EnforceVersion { get; }

            private ModIssue(string key, string expectedVersion, string actualVersion, bool enforceVersion)
            {
                Key = key;
                ExpectedVersion = expectedVersion;
                ActualVersion = actualVersion;
                EnforceVersion = enforceVersion;
            }

            public static ModIssue FromMod(string key, object mod)
            {
                return new ModIssue(
                    key,
                    GetStringProperty(mod, "Version"),
                    GetStringProperty(mod, "Version"),
                    GetBoolProperty(mod, "EnforceVersion"));
            }

            public static ModIssue FromPolicyMod(string key, PolicyMod mod)
            {
                return new ModIssue(key, mod.Version, mod.Version, mod.EnforceVersion);
            }

            public ModIssue WithActual(string actualVersion)
            {
                return new ModIssue(Key, ExpectedVersion, actualVersion, EnforceVersion);
            }
        }
    }
}
