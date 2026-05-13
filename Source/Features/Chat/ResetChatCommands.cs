using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ServideSideTweaks.Features.Chat
{
    internal static class ResetChatCommands
    {
        private static readonly int SayHash = "Say".GetStableHashCode();

        internal static bool TryConsume(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (ModConfig.EnableResetChatCommands.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return false;
            }

            if (rpcData.m_methodHash != SayHash || rpcData.m_targetZDO.IsNone())
            {
                return false;
            }

            try
            {
                rpcData.m_parameters.SetPos(0);
                rpcData.m_parameters.ReadInt();
                UserInfo userInfo = new();
                userInfo.Deserialize(ref rpcData.m_parameters);
                string text = rpcData.m_parameters.ReadString();

                if (!IsResetCommand(text))
                {
                    return false;
                }

                Vector3 position = GetChatPosition(rpcData.m_targetZDO);
                foreach (string line in BuildResponse(text))
                {
                    SendPrivateLine(rpcData.m_senderPeerID, position, userInfo, line);
                }

                return true;
            }
            catch (Exception ex)
            {
                ServideSideTweaksPlugin.ModLogger.LogWarning($"Failed to handle reset chat command: {ex}");
                return false;
            }
        }

        private static bool IsResetCommand(string text)
        {
            string trimmed = text.TrimStart();
            return trimmed.Equals("!resets", StringComparison.OrdinalIgnoreCase)
                || (trimmed.Length > "!resets".Length
                    && trimmed.StartsWith("!resets", StringComparison.OrdinalIgnoreCase)
                    && char.IsWhiteSpace(trimmed["!resets".Length]));
        }

        private static IEnumerable<string> BuildResponse(string text)
        {
            string argument = text.Trim().Length > "!resets".Length
                ? text.Trim().Substring("!resets".Length).Trim()
                : "";

            ResetDataFile.ResetDataSnapshot? snapshot = ResetDataFile.GetSnapshot(out string? lastError);
            if (snapshot == null)
            {
                return Lines(lastError == null
                    ? "Reset data is still loading. Try again in a moment."
                    : $"Reset data is unavailable: {lastError}");
            }

            if (argument.Equals("list", StringComparison.OrdinalIgnoreCase) || argument.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                string keys = string.Join(", ", snapshot.Entries.Select(entry => entry.Key));
                return Lines($"Known reset names: {keys}");
            }

            if (!string.IsNullOrEmpty(argument))
            {
                return snapshot.TryFind(argument, out ResetDataFile.ResetEntry entry)
                    ? FormatDetailed(entry)
                    : Lines($"Unknown reset \"{argument}\". Use !resets list to see valid names.");
            }

            return FormatUpcoming(snapshot);
        }

        private static IEnumerable<string> FormatUpcoming(ResetDataFile.ResetDataSnapshot snapshot)
        {
            int limit = Math.Max(1, ModConfig.ResetChatMaxUpcomingEntries.Value);
            List<ResetDataFile.ResetEntry> upcoming = snapshot.Entries
                .Where(entry => entry.Next != null)
                .OrderBy(entry => entry.Next)
                .Take(limit)
                .ToList();

            if (upcoming.Count == 0)
            {
                return Lines("No upcoming reset times are known yet. Use !resets list to see tracked reset names.");
            }

            List<string> lines = new()
            {
                "Upcoming resets:"
            };

            DateTimeOffset now = DateTimeOffset.UtcNow;
            lines.AddRange(upcoming.Select(entry => $"{entry.Label}: {FormatRelative(entry.Next, now)}"));
            return Prefix(lines);
        }

        private static IEnumerable<string> FormatDetailed(ResetDataFile.ResetEntry entry)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Prefix(new[]
            {
                entry.Label,
                $"Last: {FormatRelative(entry.Last, now)}",
                $"Next: {FormatRelative(entry.Next, now)}"
            });
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

        private static IEnumerable<string> Lines(string line)
        {
            return Prefix(new[] { line });
        }

        private static IEnumerable<string> Prefix(IEnumerable<string> lines)
        {
            return lines.Select(line => $"[Praetoris] {line}");
        }

        private static void SendPrivateLine(long targetPeerId, Vector3 position, UserInfo requester, string line)
        {
            if (ZRoutedRpc.instance == null)
            {
                return;
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(
                targetPeerId,
                "ChatMessage",
                position,
                (int)Talker.Type.Normal,
                requester,
                line);
        }

        private static Vector3 GetChatPosition(ZDOID characterId)
        {
            ZDO? zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(characterId) : null;
            return zdo != null ? zdo.GetPosition() + Vector3.up * 1.8f : Vector3.zero;
        }
    }
}
