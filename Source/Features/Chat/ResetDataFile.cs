using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;

namespace ServerSideTweaks.Features.Chat
{
    internal static class ResetDataFile
    {
        private static readonly object StateLock = new();
        private static ResetDataSnapshot? _snapshot;
        private static string? _lastError;
        private static float _nextRefreshTime;
        private static DateTime _lastWriteTimeUtc;

        internal static void Update()
        {
            if (ModConfig.EnableResetChatCommands.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            if (Time.realtimeSinceStartup < _nextRefreshTime)
            {
                return;
            }

            _nextRefreshTime = Time.realtimeSinceStartup + Mathf.Max(1.0f, ModConfig.ResetDataRefreshSeconds.Value);
            string path = GetFilePath();

            try
            {
                if (!File.Exists(path))
                {
                    lock (StateLock)
                    {
                        _snapshot = null;
                        _lastError = $"{Path.GetFileName(path)} does not exist yet.";
                        _lastWriteTimeUtc = DateTime.MinValue;
                    }

                    return;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(path);
                lock (StateLock)
                {
                    if (_snapshot != null && writeTime == _lastWriteTimeUtc)
                    {
                        return;
                    }
                }

                ResetDataSnapshot snapshot = ParseSnapshot(File.ReadAllText(path));
                lock (StateLock)
                {
                    _snapshot = snapshot;
                    _lastError = null;
                    _lastWriteTimeUtc = writeTime;
                }
            }
            catch (Exception ex)
            {
                lock (StateLock)
                {
                    _lastError = ex.Message;
                }

                ServerSideTweaksPlugin.ModLogger.LogWarning($"Failed to read reset data file: {ex.Message}");
            }
        }

        internal static ResetDataSnapshot? GetSnapshot(out string? lastError)
        {
            lock (StateLock)
            {
                lastError = _lastError;
                return _snapshot;
            }
        }

        internal static string GetFilePath()
        {
            string configured = ModConfig.ResetDataFile.Value.Trim();
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Paths.ConfigPath, configured);
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
                DateTimeOffset? last = ReadDateTime(body, "last");
                DateTimeOffset? next = ReadDateTime(body, "next");
                double? intervalSeconds = ReadDouble(body, "interval_seconds");

                if (next == null && last != null && intervalSeconds != null)
                {
                    next = last.Value.AddSeconds(intervalSeconds.Value);
                }

                entries[key] = new ResetEntry(key, label, last, next);
            }

            if (entries.Count == 0)
            {
                throw new InvalidDataException("Reset data file did not contain any reset entries.");
            }

            return new ResetDataSnapshot(entries.Values, DateTimeOffset.UtcNow);
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

        internal sealed class ResetDataSnapshot
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
        }

        internal sealed class ResetEntry
        {
            internal ResetEntry(string key, string label, DateTimeOffset? last, DateTimeOffset? next)
            {
                Key = key;
                Label = label;
                Last = last;
                Next = next;
            }

            internal string Key { get; }
            internal string Label { get; }
            internal DateTimeOffset? Last { get; }
            internal DateTimeOffset? Next { get; }
        }
    }
}
