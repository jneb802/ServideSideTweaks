using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;

namespace ServerSideTweaks.Features.ServerSigns
{
    internal static class ResetDataFile
    {
        private static readonly object StateLock = new();
        private static ResetDataSnapshot? _snapshot;
        private static string? _lastError;
        private static float _nextRefreshTime;
        private static DateTime _lastWriteTimeUtc;

        internal static void ClearRuntimeCache()
        {
            lock (StateLock)
            {
                _snapshot = null;
                _lastError = null;
                _nextRefreshTime = 0.0f;
                _lastWriteTimeUtc = DateTime.MinValue;
            }
        }

        internal static bool Update(bool force = false)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return false;
            }

            if (!force && Time.realtimeSinceStartup < _nextRefreshTime)
            {
                return false;
            }

            _nextRefreshTime = Time.realtimeSinceStartup + Mathf.Max(1.0f, ModConfig.ServerSignResetDataRefreshSeconds.Value);
            string path = GetFilePath();

            try
            {
                if (!File.Exists(path))
                {
                    string error = $"{Path.GetFileName(path)} does not exist yet.";
                    bool changed;
                    lock (StateLock)
                    {
                        changed = _snapshot != null || !string.Equals(_lastError, error, StringComparison.Ordinal);
                        _snapshot = null;
                        _lastError = error;
                        _lastWriteTimeUtc = DateTime.MinValue;
                    }

                    return changed;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(path);
                lock (StateLock)
                {
                    if (_snapshot != null && writeTime == _lastWriteTimeUtc)
                    {
                        return false;
                    }
                }

                ResetDataSnapshot snapshot = ParseSnapshot(File.ReadAllText(path));
                lock (StateLock)
                {
                    _snapshot = snapshot;
                    _lastError = null;
                    _lastWriteTimeUtc = writeTime;
                }

                return true;
            }
            catch (Exception ex)
            {
                bool changed;
                lock (StateLock)
                {
                    changed = !string.Equals(_lastError, ex.Message, StringComparison.Ordinal);
                    _lastError = ex.Message;
                }

                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to read reset data file: {ex.Message}");
                return changed;
            }
        }

        internal static bool TryBuildSignText(string resetName, string resetFilter, string size, string alignment, out string text)
        {
            Update(force: false);
            string normalizedAlignment = SignTextAlignment.NormalizeOrDefault(alignment);
            ResetDataSnapshot? snapshot = GetSnapshot(out string? lastError);
            if (snapshot == null)
            {
                text = RenderMessage("World Resets", lastError ?? "Reset data is still loading.", size, normalizedAlignment);
                return true;
            }

            string trimmed = resetName.Trim();
            if (trimmed.Length > 0 && !trimmed.Equals("summary", StringComparison.OrdinalIgnoreCase))
            {
                if (TryNormalizeCategory(trimmed, out string category))
                {
                    text = BuildCategoryText(snapshot, category, resetFilter, size, normalizedAlignment);
                    return true;
                }

                text = snapshot.TryFind(trimmed, out ResetEntry entry)
                    ? RenderDetail(entry, size, normalizedAlignment)
                    : RenderMessage("World Resets", $"Unknown reset: {trimmed}", size, normalizedAlignment);
                return true;
            }

            text = RenderUpcoming(snapshot.Entries, "World Resets", "Location, dungeon, vegetation", size, normalizedAlignment);
            return true;
        }

        private static string BuildCategoryText(ResetDataSnapshot snapshot, string category, string resetFilter, string size, string alignment)
        {
            if (category == "vegetation" && string.IsNullOrWhiteSpace(resetFilter))
            {
                return snapshot.TryFind("vegetation", out ResetEntry vegetation)
                    ? RenderDetail(vegetation, size, alignment)
                    : RenderMessage("Vegetation Resets", "Unknown reset: vegetation", size, alignment);
            }

            if (TryParseFilter(resetFilter, out string filterName, out string filterValue))
            {
                if (category == "location" && filterName != "biome")
                {
                    return RenderMessage("Location Resets", "Use biome=<biome> with location resets.", size, alignment);
                }

                if (category == "dungeon" && filterName != "biome")
                {
                    return RenderMessage("Dungeon Resets", "Use biome=<biome> with dungeon resets.", size, alignment);
                }

                if (category == "vegetation" && filterName != "vegetation")
                {
                    return RenderMessage("Vegetation Resets", "Use vegetation=<key> with vegetation resets.", size, alignment);
                }

                return snapshot.TryFind(category, filterName, filterValue, out ResetEntry entry)
                    ? RenderDetail(entry, size, alignment)
                    : RenderMessage(CategoryTitle(category), $"Unknown {filterName}: {filterValue}", size, alignment);
            }

            if (!string.IsNullOrWhiteSpace(resetFilter))
            {
                return RenderMessage(CategoryTitle(category), $"Unknown reset filter: {resetFilter}", size, alignment);
            }

            List<ResetEntry> entries = snapshot.Entries
                .Where(entry => string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (entries.Count == 0 && category == "location" && snapshot.TryFind("locations", out ResetEntry legacyLocations))
            {
                return RenderDetail(legacyLocations, size, alignment);
            }

            return entries.Count > 0
                ? RenderUpcoming(entries, CategoryTitle(category), CategorySubheading(category), size, alignment)
                : RenderMessage(CategoryTitle(category), "No reset data found.", size, alignment);
        }

        private static ResetDataSnapshot? GetSnapshot(out string? lastError)
        {
            lock (StateLock)
            {
                lastError = _lastError;
                return _snapshot;
            }
        }

        private static string GetFilePath()
        {
            string configured = ModConfig.ServerSignResetDataFile.Value.Trim();
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Paths.ConfigPath, configured.Length > 0 ? configured : "praetoris_resets.json");
        }

        private static ResetDataSnapshot ParseSnapshot(string raw)
        {
            Dictionary<string, ResetEntry> entries = new(StringComparer.OrdinalIgnoreCase);
            Match resetsMatch = Regex.Match(raw, "\"resets\"\\s*:\\s*\\{(?<body>.*)\\}\\s*\\}?", RegexOptions.Singleline);
            if (!resetsMatch.Success)
            {
                throw new InvalidDataException("Reset data file does not contain a resets object.");
            }

            foreach (Match entryMatch in Regex.Matches(
                resetsMatch.Groups["body"].Value,
                "\"(?<key>[^\"]+)\"\\s*:\\s*\\{(?<body>.*?)\\}\\s*,?",
                RegexOptions.Singleline))
            {
                string key = entryMatch.Groups["key"].Value;
                string body = entryMatch.Groups["body"].Value;
                string label = ReadString(body, "label") ?? key;
                string category = ReadString(body, "category") ?? "";
                string biome = ReadString(body, "biome") ?? "";
                string vegetation = ReadString(body, "vegetation") ?? "";
                DateTimeOffset? last = ReadDateTime(body, "last");
                DateTimeOffset? next = ReadDateTime(body, "next");
                double? intervalSeconds = ReadDouble(body, "interval_seconds");

                if (next == null && last != null && intervalSeconds != null)
                {
                    next = last.Value.AddSeconds(intervalSeconds.Value);
                }

                entries[key] = new ResetEntry(key, label, category, biome, vegetation, last, next);
            }

            if (entries.Count == 0)
            {
                throw new InvalidDataException("Reset data file did not contain any reset entries.");
            }

            return new ResetDataSnapshot(entries.Values, DateTimeOffset.UtcNow);
        }

        private static string RenderUpcoming(IEnumerable<ResetEntry> resetEntries, string title, string subheading, string size, string alignment)
        {
            List<ResetEntry> upcoming = resetEntries
                .Where(entry => entry.Next != null)
                .OrderBy(entry => entry.Next)
                .Take(5)
                .ToList();

            List<string> lines = new()
            {
                Heading(title, size, alignment),
                Subheading(subheading, size, alignment),
            };

            if (upcoming.Count == 0)
            {
                lines.Add(Row("Next", "unknown", size, alignment));
                return string.Join("\n", lines);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            lines.AddRange(upcoming.Select(entry => Row(ShortLabel(entry.Label), FormatRelative(entry.Next, now), size, alignment)));
            return string.Join("\n", lines);
        }

        private static string RenderDetail(ResetEntry entry, string size, string alignment)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return string.Join("\n", new[]
            {
                Heading(entry.Label, size, alignment),
                Subheading("Reset schedule", size, alignment),
                Row("Last", FormatRelative(entry.Last, now), size, alignment),
                Row("Next", FormatRelative(entry.Next, now), size, alignment),
            });
        }

        private static string RenderMessage(string title, string message, string size, string alignment)
        {
            return string.Join("\n", new[]
            {
                Heading(title, size, alignment),
                Subheading("Reset schedule", size, alignment),
                Body(message, size, alignment),
            });
        }

        private static string Heading(string value, string size, string alignment)
        {
            return Line($"<size={FormatSize(1.35f, size)}><color=#ffd166><b>{EscapeRichText(value)}</b></color></size>", alignment);
        }

        private static string Subheading(string value, string size, string alignment)
        {
            return Line($"<size={FormatSize(0.65f, size)}><color=#9be7ff>{EscapeRichText(value)}</color></size>", alignment);
        }

        private static string Row(string label, string value, string size, string alignment)
        {
            return Line($"<size={FormatSize(0.75f, size)}><color=#ffffff><b>{EscapeRichText(label)}:</b> {EscapeRichText(value)}</color></size>", alignment);
        }

        private static string Body(string value, string size, string alignment)
        {
            return Line($"<size={FormatSize(0.75f, size)}><color=#ffffff>{EscapeRichText(value)}</color></size>", alignment);
        }

        private static string Line(string value, string alignment)
        {
            return $"<align=\"{SignTextAlignment.NormalizeOrDefault(alignment)}\"><nobr>{value}</nobr></align>";
        }

        private static string FormatSize(float baseSize, string size)
        {
            float scale = SignTextScale.ToMultiplier(size);
            return (baseSize * scale).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string ShortLabel(string value)
        {
            return value
                .Replace(" Reset", "")
                .Replace(" reset", "")
                .Replace("Node", "")
                .Replace("node", "")
                .Trim();
        }

        private static bool TryNormalizeCategory(string value, out string category)
        {
            string normalized = NormalizeToken(value);
            switch (normalized)
            {
                case "location":
                case "locations":
                    category = "location";
                    return true;
                case "dungeon":
                case "dungeons":
                    category = "dungeon";
                    return true;
                case "vegetation":
                    category = "vegetation";
                    return true;
                default:
                    category = "";
                    return false;
            }
        }

        private static bool TryParseFilter(string resetFilter, out string name, out string value)
        {
            name = "";
            value = "";
            string trimmed = resetFilter.Trim();
            int separator = trimmed.IndexOf("=", StringComparison.Ordinal);
            if (separator <= 0 || separator >= trimmed.Length - 1)
            {
                return false;
            }

            name = NormalizeToken(trimmed.Substring(0, separator));
            value = trimmed.Substring(separator + 1).Trim();
            return name.Length > 0 && value.Length > 0;
        }

        private static string CategoryTitle(string category)
        {
            switch (category)
            {
                case "location":
                    return "Location Resets";
                case "dungeon":
                    return "Dungeon Resets";
                case "vegetation":
                    return "Vegetation Resets";
                default:
                    return "World Resets";
            }
        }

        private static string CategorySubheading(string category)
        {
            switch (category)
            {
                case "location":
                    return "By biome";
                case "dungeon":
                    return "Dungeons and forts";
                case "vegetation":
                    return "By resource";
                default:
                    return "Reset schedule";
            }
        }

        private static string FormatRelative(DateTimeOffset? value, DateTimeOffset now)
        {
            if (value == null)
            {
                return "unknown";
            }

            TimeSpan delta = value.Value - now;
            string formatted = FormatDuration(delta.Duration());
            return delta.TotalSeconds >= 0.0 ? $"in {formatted}" : $"{formatted} ago";
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes < 1.0)
            {
                return "less than 1m";
            }

            if (duration.TotalHours < 1.0)
            {
                return $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))}m";
            }

            if (duration.TotalDays < 1.0)
            {
                int hours = (int)duration.TotalHours;
                int minutes = duration.Minutes;
                return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
            }

            int days = (int)duration.TotalDays;
            int remainingHours = duration.Hours;
            return remainingHours > 0 ? $"{days}d {remainingHours}h" : $"{days}d";
        }

        private static string EscapeRichText(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static string? ReadString(string body, string field)
        {
            Match match = Regex.Match(body, $"\"{Regex.Escape(field)}\"\\s*:\\s*(?:\"(?<value>[^\"]*)\"|null)", RegexOptions.Singleline);
            return match.Success ? match.Groups["value"].Value : null;
        }

        private static double? ReadDouble(string body, string field)
        {
            Match match = Regex.Match(body, $"\"{Regex.Escape(field)}\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)", RegexOptions.Singleline);
            return match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : null;
        }

        private static DateTimeOffset? ReadDateTime(string body, string field)
        {
            string? text = ReadString(body, field);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset value)
                ? value
                : null;
        }

        private sealed class ResetDataSnapshot
        {
            private readonly Dictionary<string, ResetEntry> _entriesByAlias;

            internal ResetDataSnapshot(IEnumerable<ResetEntry> entries, DateTimeOffset fetchedAt)
            {
                Entries = entries.OrderBy(entry => entry.Next ?? DateTimeOffset.MaxValue).ToList();
                FetchedAt = fetchedAt;
                _entriesByAlias = new Dictionary<string, ResetEntry>(StringComparer.OrdinalIgnoreCase);

                foreach (ResetEntry entry in Entries)
                {
                    AddAlias(entry.Key, entry);
                    AddAlias(entry.Label, entry);
                    AddAlias(entry.Key.Replace("_", ""), entry);
                    AddAlias(entry.Label.Replace(" ", ""), entry);
                }

                AddAlias("bf", "blackforest_dungeons");
                AddAlias("blackforest", "blackforest_dungeons");
                AddAlias("blackforestdungeons", "blackforest_dungeons");
                AddAlias("burialchambers", "blackforest_dungeons");
                AddAlias("trollcaves", "blackforest_dungeons");
                AddAlias("swamp", "swamp_dungeons");
                AddAlias("crypt", "swamp_dungeons");
                AddAlias("crypts", "swamp_dungeons");
                AddAlias("swampcrypts", "swamp_dungeons");
                AddAlias("plants", "wild_plants");
                AddAlias("wildplants", "wild_plants");
                AddAlias("carrots", "wild_plants");
                AddAlias("turnips", "wild_plants");
            }

            internal IReadOnlyList<ResetEntry> Entries { get; }
            internal DateTimeOffset FetchedAt { get; }

            internal bool TryFind(string query, out ResetEntry entry)
            {
                return _entriesByAlias.TryGetValue(Normalize(query), out entry);
            }

            internal bool TryFind(string category, string filterName, string filterValue, out ResetEntry entry)
            {
                string normalizedCategory = Normalize(category);
                string normalizedFilterValue = Normalize(filterValue);
                entry = Entries.FirstOrDefault(candidate =>
                    Normalize(candidate.Category) == normalizedCategory &&
                    Normalize(FilterValue(candidate, filterName)) == normalizedFilterValue);
                return entry != null;
            }

            private void AddAlias(string alias, ResetEntry entry)
            {
                string normalized = Normalize(alias);
                if (!string.IsNullOrEmpty(normalized))
                {
                    _entriesByAlias[normalized] = entry;
                }
            }

            private void AddAlias(string alias, string targetKey)
            {
                ResetEntry? target = Entries.FirstOrDefault(entry => entry.Key.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                {
                    AddAlias(alias, target);
                }
            }

            private static string Normalize(string value)
            {
                return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            }

            private static string FilterValue(ResetEntry entry, string filterName)
            {
                switch (Normalize(filterName))
                {
                    case "biome":
                        return entry.Biome;
                    case "vegetation":
                        return entry.Vegetation.Length > 0 ? entry.Vegetation : entry.Key;
                    default:
                        return "";
                }
            }
        }

        private sealed class ResetEntry
        {
            internal ResetEntry(string key, string label, string category, string biome, string vegetation, DateTimeOffset? last, DateTimeOffset? next)
            {
                Key = key;
                Label = label;
                Category = category;
                Biome = biome;
                Vegetation = vegetation;
                Last = last;
                Next = next;
            }

            internal string Key { get; }
            internal string Label { get; }
            internal string Category { get; }
            internal string Biome { get; }
            internal string Vegetation { get; }
            internal DateTimeOffset? Last { get; }
            internal DateTimeOffset? Next { get; }
        }

        private static string NormalizeToken(string value)
        {
            return new string((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }
    }
}
