using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace ServerSideTweaks.Features.ValheimEnforcer
{
    internal static class ValheimEnforcerKickAlerts
    {
        private const float DuplicateWindowSeconds = 90.0f;
        private static readonly Dictionary<string, float> RecentAlerts = new();
        private static bool _patched;

        internal static void TryPatch(Harmony harmony)
        {
            if (_patched)
            {
                return;
            }

            MethodInfo? target = TargetMethod();
            if (target == null)
            {
                DebugLog("ValheimEnforcer ModManager RPC not found. Kick alerts are inactive.");
                return;
            }

            MethodInfo prefix = AccessTools.Method(typeof(ValheimEnforcerKickAlerts), nameof(Prefix));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            _patched = true;
            ServerSideTweaksPlugin.ModLogger.LogInfo("Patched ValheimEnforcer mod validation for Discord kick alerts.");
        }

        private static MethodInfo? TargetMethod()
        {
            Type? modManager = AccessTools.TypeByName("ValheimEnforcer.modules.ModManager");
            return modManager == null
                ? null
                : AccessTools.Method(modManager, "RPC_ReceiveModVersionData");
        }

        private static void Prefix(ZRpc sender, ZPackage data)
        {
            if (!ModConfig.EnableValheimEnforcerKickAlerts.Value ||
                ZNet.instance == null ||
                !ZNet.instance.IsServer() ||
                sender == null ||
                data == null)
            {
                return;
            }

            try
            {
                ValidationReport? report = BuildValidationReport(sender, data);
                if (report == null || report.IsValid)
                {
                    return;
                }

                string duplicateKey = report.PlayerId + "|" + report.Fingerprint;
                float now = Time.realtimeSinceStartup;
                if (RecentAlerts.TryGetValue(duplicateKey, out float lastSent) &&
                    now - lastSent < DuplicateWindowSeconds)
                {
                    DebugLog("Skipping duplicate ValheimEnforcer kick alert for " + report.PlayerId + ".");
                    return;
                }

                RecentAlerts[duplicateKey] = now;
                ServerSideTweaksPlugin.ModLogger.LogInfo(
                    "ValheimEnforcer rejected " + report.DescribePlayer() + ": " + report.Summary);

                ServerSideTweaksPlugin.Instance?.StartCoroutine(SendAlert(report));
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning("Failed to inspect ValheimEnforcer validation failure: " + ex);
            }
        }

        private static ValidationReport? BuildValidationReport(ZRpc sender, ZPackage data)
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

            var packageCopy = new ZPackage(data.GetArray());
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

            ModDictionary active = GetMods(clientMods, "ActiveMods");
            ModDictionary required = GetMods(serverMods, "RequiredMods");
            ModDictionary optional = GetMods(serverMods, "OptionalMods");
            ModDictionary adminOnly = GetMods(serverMods, "AdminOnlyMods");

            var missing = new List<ModIssue>();
            var mismatches = new List<ModIssue>();
            var extras = new List<ModIssue>();

            foreach (var requiredMod in required.Values)
            {
                if (!active.ContainsKey(requiredMod.Key))
                {
                    missing.Add(requiredMod.WithActual(""));
                }
            }

            foreach (var clientMod in active.Values)
            {
                if (required.TryGetValue(clientMod.Key, out ModIssue requiredMod))
                {
                    AddMismatchIfNeeded(mismatches, requiredMod, clientMod, enforceForThisPlayer: true);
                    continue;
                }

                if (adminOnly.TryGetValue(clientMod.Key, out ModIssue adminMod))
                {
                    if (isAdmin)
                    {
                        AddMismatchIfNeeded(mismatches, adminMod, clientMod, enforceForThisPlayer: true);
                    }
                    else
                    {
                        extras.Add(clientMod);
                    }

                    continue;
                }

                if (optional.TryGetValue(clientMod.Key, out ModIssue optionalMod))
                {
                    AddMismatchIfNeeded(mismatches, optionalMod, clientMod, enforceForThisPlayer: true);
                    continue;
                }

                extras.Add(clientMod);
            }

            return new ValidationReport(playerId, playerName, endpoint, missing, mismatches, extras);
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

        private static IEnumerator SendAlert(ValidationReport report)
        {
            string alertUrl = ModConfig.ValheimEnforcerKickAlertBotUrl.Value.Trim();
            if (string.IsNullOrWhiteSpace(alertUrl))
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning(
                    "ValheimEnforcer kick alerts are enabled, but KickAlertBotUrl is not configured.");
                yield break;
            }

            bool isDiscordWebhook = IsDiscordWebhookUrl(alertUrl);
            string body = isDiscordWebhook
                ? BuildDiscordWebhookBody(report)
                : BuildBotAlertBody(report);
            using var request = new UnityWebRequest(alertUrl, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            request.SetRequestHeader("Content-Type", "application/json");
            if (!isDiscordWebhook)
            {
                string apiKey = ModConfig.ValheimEnforcerBotApiKey.Value.Trim();
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.SetRequestHeader("X-API-Key", apiKey);
                }
            }
            request.SetRequestHeader("User-Agent", "serverSideTweaks/1.1");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success ||
                request.responseCode < 200 ||
                request.responseCode >= 300)
            {
                string response = request.downloadHandler?.text ?? "";
                ServerSideTweaksPlugin.ModLogger.LogWarning(
                    "ValheimEnforcer kick alert failed: " + request.error +
                    " HTTP " + request.responseCode + " " + response);
            }
            else
            {
                DebugLog("Sent ValheimEnforcer kick alert for " + report.DescribePlayer() + ".");
            }
        }

        private static bool IsDiscordWebhookUrl(string alertUrl)
        {
            if (!Uri.TryCreate(alertUrl, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool isDiscordHost = string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase) ||
                                 uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(uri.Host, "discordapp.com", StringComparison.OrdinalIgnoreCase) ||
                                 uri.Host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);
            return isDiscordHost &&
                   uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDiscordWebhookBody(ValidationReport report)
        {
            string playerName = string.IsNullOrWhiteSpace(report.PlayerName)
                ? "A player"
                : report.PlayerName.Trim();
            string content = playerName + " failed to join the server because of a mod mismatch:";
            string description = BuildDiscordDescription(report);
            if (description.Length > 3900)
            {
                description = description.Substring(0, 3900);
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("{");
            AppendJsonProperty(builder, "content", content);
            if (!string.IsNullOrWhiteSpace(description))
            {
                builder.Append(",\"embeds\":[{");
                AppendJsonProperty(builder, "description", description);
                builder.Append(",\"color\":15158332}]");
            }

            builder.Append(",\"allowed_mentions\":{\"parse\":[]}}");
            return builder.ToString();
        }

        private static string BuildDiscordDescription(ValidationReport report)
        {
            StringBuilder builder = new StringBuilder();
            AppendDiscordIssueBlock(builder, "Remove these mods", report.ExtraMods, "remove");
            AppendDiscordIssueBlock(builder, "Change these mods", report.VersionMismatches, "change");
            AppendDiscordIssueBlock(builder, "Add these mods", report.MissingRequiredMods, "add");
            return builder.ToString();
        }

        private static void AppendDiscordIssueBlock(
            StringBuilder builder,
            string title,
            IReadOnlyList<ModIssue> issues,
            string issueType)
        {
            if (issues.Count == 0)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append("**");
            builder.Append(title);
            builder.Append("**\n");
            if (string.Equals(issueType, "change", StringComparison.Ordinal))
            {
                builder.Append("Current version --> required version\n");
            }

            int issueCount = Math.Min(issues.Count, 12);
            for (int i = 0; i < issueCount; i++)
            {
                ModIssue issue = issues[i];
                builder.Append("- ");
                builder.Append(FormatDiscordMod(issue));
                if (string.Equals(issueType, "change", StringComparison.Ordinal))
                {
                    builder.Append(": `");
                    builder.Append(string.IsNullOrWhiteSpace(issue.ActualVersion) ? "unknown" : issue.ActualVersion);
                    builder.Append("` --> `");
                    builder.Append(string.IsNullOrWhiteSpace(issue.ExpectedVersion) ? "unknown" : issue.ExpectedVersion);
                    builder.Append("`");
                }
                else if (string.Equals(issueType, "add", StringComparison.Ordinal))
                {
                    string expectedVersion = !string.IsNullOrWhiteSpace(issue.ExpectedVersion)
                        ? issue.ExpectedVersion
                        : !string.IsNullOrWhiteSpace(issue.PackageVersion)
                            ? issue.PackageVersion
                            : "unknown";
                    builder.Append(" `");
                    builder.Append(expectedVersion);
                    builder.Append("`");
                }

                builder.Append("\n");
            }

            if (issues.Count > issueCount)
            {
                builder.Append("- ...and ");
                builder.Append(issues.Count - issueCount);
                builder.Append(" more\n");
            }
        }

        private static string FormatDiscordMod(ModIssue issue)
        {
            string label = !string.IsNullOrWhiteSpace(issue.Name)
                ? issue.Name
                : !string.IsNullOrWhiteSpace(issue.PluginId)
                    ? issue.PluginId
                    : "unknown";
            if (issue.ThunderstoreVersionUrl.StartsWith("https://thunderstore.io/", StringComparison.Ordinal) ||
                issue.ThunderstoreVersionUrl.StartsWith("https://new.thunderstore.io/", StringComparison.Ordinal))
            {
                return "[" + EscapeMarkdownLinkLabel(label) + "](" + issue.ThunderstoreVersionUrl + ")";
            }

            return "`" + label.Replace("`", "") + "`";
        }

        private static string EscapeMarkdownLinkLabel(string value)
        {
            return value.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
        }

        private static string BuildBotAlertBody(ValidationReport report)
        {
            var builder = new StringBuilder();
            builder.Append("{");
            AppendJsonProperty(builder, "player_id", report.PlayerId);
            builder.Append(",");
            AppendJsonProperty(builder, "player_name", report.PlayerName);
            builder.Append(",");
            AppendJsonProperty(builder, "endpoint", report.Endpoint);
            builder.Append(",");
            AppendIssueArray(builder, "missing_required_mods", report.MissingRequiredMods);
            builder.Append(",");
            AppendIssueArray(builder, "version_mismatches", report.VersionMismatches);
            builder.Append(",");
            AppendIssueArray(builder, "extra_mods", report.ExtraMods);
            builder.Append("}");
            return builder.ToString();
        }

        private static void AppendJsonProperty(StringBuilder builder, string name, string value)
        {
            builder.Append("\"");
            builder.Append(EscapeJson(name));
            builder.Append("\":\"");
            builder.Append(EscapeJson(value));
            builder.Append("\"");
        }

        private static void AppendIssueArray(StringBuilder builder, string name, IReadOnlyList<ModIssue> issues)
        {
            builder.Append("\"");
            builder.Append(EscapeJson(name));
            builder.Append("\":[");
            for (int i = 0; i < issues.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                ModIssue issue = issues[i];
                builder.Append("{");
                AppendJsonProperty(builder, "name", issue.Name);
                builder.Append(",");
                AppendJsonProperty(builder, "plugin_id", issue.PluginId);
                builder.Append(",");
                AppendJsonProperty(builder, "expected_version", issue.ExpectedVersion);
                builder.Append(",");
                AppendJsonProperty(builder, "actual_version", issue.ActualVersion);
                builder.Append(",");
                AppendJsonProperty(builder, "package_owner", issue.PackageOwner);
                builder.Append(",");
                AppendJsonProperty(builder, "package_name", issue.PackageName);
                builder.Append(",");
                AppendJsonProperty(builder, "package_version", issue.PackageVersion);
                builder.Append(",");
                AppendJsonProperty(builder, "thunderstore_version_url", issue.ThunderstoreVersionUrl);
                builder.Append("}");
            }

            builder.Append("]");
        }

        private static ModDictionary GetMods(object mods, string propertyName)
        {
            var result = new ModDictionary();
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

        private static string EscapeJson(string value)
        {
            return (value ?? "")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
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
            if (ModConfig.DebugValheimEnforcerKickAlerts.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo("[ValheimEnforcerKickAlerts] " + message);
            }
        }

        private sealed class ModDictionary : Dictionary<string, ModIssue>
        {
        }

        private sealed class ModIssue
        {
            public string Key { get; }
            public string Name { get; }
            public string PluginId { get; }
            public string ExpectedVersion { get; }
            public string ActualVersion { get; }
            public bool EnforceVersion { get; }
            public string PackageOwner { get; }
            public string PackageName { get; }
            public string PackageVersion { get; }
            public string ThunderstoreVersionUrl { get; }

            private ModIssue(
                string key,
                string name,
                string pluginId,
                string expectedVersion,
                string actualVersion,
                bool enforceVersion,
                ThunderstorePackage package)
            {
                Key = key;
                Name = name;
                PluginId = pluginId;
                ExpectedVersion = expectedVersion;
                ActualVersion = actualVersion;
                EnforceVersion = enforceVersion;
                PackageOwner = package.Owner;
                PackageName = package.Name;
                PackageVersion = package.Version;
                ThunderstoreVersionUrl = package.VersionUrl;
            }

            public static ModIssue FromMod(string key, object mod)
            {
                string pluginId = GetStringProperty(mod, "PluginID");
                return new ModIssue(
                    key,
                    GetStringProperty(mod, "Name"),
                    pluginId,
                    GetStringProperty(mod, "Version"),
                    GetStringProperty(mod, "Version"),
                    GetBoolProperty(mod, "EnforceVersion"),
                    ThunderstorePackage.ForPlugin(pluginId));
            }

            public ModIssue WithActual(string actualVersion)
            {
                return new ModIssue(
                    Key,
                    Name,
                    PluginId,
                    ExpectedVersion,
                    actualVersion,
                    EnforceVersion,
                    new ThunderstorePackage(PackageOwner, PackageName, PackageVersion));
            }
        }

        private readonly struct ThunderstorePackage
        {
            private static readonly Dictionary<string, ThunderstorePackage> Cache = new();

            public string Owner { get; }
            public string Name { get; }
            public string Version { get; }

            public string VersionUrl =>
                string.IsNullOrWhiteSpace(Owner) ||
                string.IsNullOrWhiteSpace(Name) ||
                string.IsNullOrWhiteSpace(Version)
                    ? ""
                    : "https://thunderstore.io/c/valheim/p/" + Owner + "/" + Name + "/v/" + Version + "/";

            public ThunderstorePackage(string owner, string name, string version)
            {
                Owner = owner;
                Name = name;
                Version = version;
            }

            public static ThunderstorePackage ForPlugin(string pluginId)
            {
                if (string.IsNullOrWhiteSpace(pluginId))
                {
                    return default;
                }

                if (Cache.TryGetValue(pluginId, out ThunderstorePackage cached))
                {
                    return cached;
                }

                ThunderstorePackage package = LoadForPlugin(pluginId);
                Cache[pluginId] = package;
                return package;
            }

            private static ThunderstorePackage LoadForPlugin(string pluginId)
            {
                if (!Chainloader.PluginInfos.TryGetValue(pluginId, out PluginInfo pluginInfo))
                {
                    return default;
                }

                string pluginPath = pluginInfo.Location;
                if (string.IsNullOrWhiteSpace(pluginPath))
                {
                    return default;
                }

                string packageRoot = FindPackageRoot(pluginPath);
                if (string.IsNullOrWhiteSpace(packageRoot))
                {
                    return default;
                }

                string folderName = Path.GetFileName(packageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                int separator = folderName.IndexOf('-');
                if (separator <= 0 || separator >= folderName.Length - 1)
                {
                    return default;
                }

                string owner = folderName.Substring(0, separator);
                string manifestPath = Path.Combine(packageRoot, "manifest.json");
                string manifestText = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : "";
                string manifestName = ReadManifestString(manifestText, "name");
                string manifestVersion = ReadManifestString(manifestText, "version_number");
                string packageName = string.IsNullOrWhiteSpace(manifestName)
                    ? StripVersionSuffix(folderName.Substring(separator + 1), manifestVersion)
                    : manifestName;

                return new ThunderstorePackage(owner, packageName, manifestVersion);
            }

            private static string FindPackageRoot(string pluginPath)
            {
                string pluginRoot = Paths.PluginPath;
                if (string.IsNullOrWhiteSpace(pluginRoot))
                {
                    return "";
                }

                string fullPluginRoot = Path.GetFullPath(pluginRoot);
                string current = Path.GetFullPath(Path.GetDirectoryName(pluginPath) ?? "");
                while (!string.IsNullOrWhiteSpace(current) &&
                       current.StartsWith(fullPluginRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(current, "manifest.json")))
                    {
                        return current;
                    }

                    string? parent = Path.GetDirectoryName(current);
                    if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    current = parent ?? "";
                }

                return "";
            }

            private static string StripVersionSuffix(string packageName, string version)
            {
                string suffix = "-" + version;
                return !string.IsNullOrWhiteSpace(version) && packageName.EndsWith(suffix, StringComparison.Ordinal)
                    ? packageName.Substring(0, packageName.Length - suffix.Length)
                    : packageName;
            }

            private static string ReadManifestString(string manifestText, string name)
            {
                if (string.IsNullOrWhiteSpace(manifestText))
                {
                    return "";
                }

                Match match = Regex.Match(
                    manifestText,
                    "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"");
                return match.Success ? match.Groups["value"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : "";
            }
        }

        private sealed class ValidationReport
        {
            public string PlayerId { get; }
            public string PlayerName { get; }
            public string Endpoint { get; }
            public IReadOnlyList<ModIssue> MissingRequiredMods { get; }
            public IReadOnlyList<ModIssue> VersionMismatches { get; }
            public IReadOnlyList<ModIssue> ExtraMods { get; }

            public ValidationReport(
                string playerId,
                string playerName,
                string endpoint,
                IReadOnlyList<ModIssue> missingRequiredMods,
                IReadOnlyList<ModIssue> versionMismatches,
                IReadOnlyList<ModIssue> extraMods)
            {
                PlayerId = playerId;
                PlayerName = playerName;
                Endpoint = endpoint;
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

            public string Fingerprint =>
                string.Join(",", MissingRequiredMods.Select(issue => "missing:" + issue.Key)
                    .Concat(VersionMismatches.Select(issue => "version:" + issue.Key + ":" + issue.ActualVersion))
                    .Concat(ExtraMods.Select(issue => "extra:" + issue.Key)));

            public string DescribePlayer()
            {
                return DisplayPlayer() + " endpoint=" + Endpoint;
            }

            public string DisplayPlayer()
            {
                if (!string.IsNullOrWhiteSpace(PlayerName) && !string.IsNullOrWhiteSpace(PlayerId))
                {
                    return PlayerName + " (`" + PlayerId + "`)";
                }

                return string.IsNullOrWhiteSpace(PlayerId) ? "unknown" : "`" + PlayerId + "`";
            }
        }
    }
}
