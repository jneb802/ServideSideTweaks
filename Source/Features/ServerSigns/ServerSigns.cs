using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Newtonsoft.Json;
using ServerSideTweaks.Infrastructure.Routing;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ServerSideTweaks.Features.ServerSigns
{
    internal static class ServerSigns
    {
        private const string SignPrefabName = "sign";
        private const string SupportPrefabName = "wood_pole";
        private const string ServerAuthorDisplayName = "Server";
        private const string DynamicSource = "dynamic";
        private const string DefaultRegistryFileName = "warpalicious.serverSideTweaks.serverSigns.json";
        private static readonly int SignPrefabHash = SignPrefabName.GetStableHashCode();
        private static readonly Dictionary<ZDOID, ServerSignRegistration> Signs = new();
        private static readonly Dictionary<string, float> DynamicSignNextRefreshTimes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<ZDO> SignScanBuffer = new();
        private static readonly Queue<ZDOID> PendingSignWriteOrder = new();
        private static readonly Dictionary<ZDOID, PendingSignWrite> PendingSignWrites = new();
        private static readonly Dictionary<long, float> NextPlayerSignCommandTimes = new();
        private static readonly ServerSignMetrics Metrics = new();
        private static float _nextUpdateTime;
        private static float _nextMetricsLogTime;
        private static bool _registryLoaded;

        internal static void ClearRuntimeCache()
        {
            Signs.Clear();
            SignScanBuffer.Clear();
            _nextUpdateTime = 0.0f;
            _registryLoaded = false;
            DynamicSignNextRefreshTimes.Clear();
            PendingSignWriteOrder.Clear();
            PendingSignWrites.Clear();
            NextPlayerSignCommandTimes.Clear();
            Metrics.Clear();
            _nextMetricsLogTime = 0.0f;
            ResetDataFile.ClearRuntimeCache();
        }

        internal static void RegisterConsoleCommands()
        {
            new Terminal.ConsoleCommand(
                "sst_signs_scan",
                "Scans supported server sign commands.",
                args => ScanAndUpdateSigns(force: true),
                onlyAdmin: true,
                remoteCommand: true);

            new Terminal.ConsoleCommand(
                "sst_signs_examples",
                "Creates supported !reset signs near a connected player.",
                args => CreateDynamicSignExamples(args.Length >= 2 ? args[1] : null),
                onlyAdmin: true,
                remoteCommand: true);
        }

        internal static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Register("ChatMessage", HandleChatMessage);
            RoutedRpcDispatcher.Register("Say", HandleSayMessage);
        }

        internal static void Update()
        {
            if (!IsEnabled())
            {
                return;
            }

            ProcessPendingSignWrites();
            MaybeLogMetrics();
            if (Time.time < _nextUpdateTime)
            {
                return;
            }

            _nextUpdateTime = Time.time + Mathf.Max(1.0f, ModConfig.ServerSignUpdateIntervalSeconds.Value);
            float resetReadStart = Time.realtimeSinceStartup;
            bool resetDataChanged = ResetDataFile.Update();
            Metrics.RecordResetFileCheck((Time.realtimeSinceStartup - resetReadStart) * 1000.0f, resetDataChanged);
            RefreshRegisteredSigns(resetDataChanged);
        }

        private static RoutedRpcAction HandleChatMessage(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!TryReadChatMessage(rpcData, out Vector3 position, out UserInfo userInfo, out string text) ||
                !IsSignCommand(text))
            {
                return RoutedRpcAction.Continue;
            }

            string response = HandlePlayerSignCommand(rpcData.m_senderPeerID, position);
            SendPrivateChatMessage(rpcData.m_senderPeerID, position, userInfo, response);
            return RoutedRpcAction.Consume;
        }

        private static RoutedRpcAction HandleSayMessage(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!TryReadSayMessage(rpcData, out UserInfo userInfo, out string text) ||
                !IsSignCommand(text))
            {
                return RoutedRpcAction.Continue;
            }

            Vector3 position = Vector3.zero;
            if (ZNet.instance != null)
            {
                ZNetPeer? peer = ZNet.instance.GetPeer(rpcData.m_senderPeerID);
                if (peer != null)
                {
                    position = peer.GetRefPos();
                }
            }

            string response = HandlePlayerSignCommand(rpcData.m_senderPeerID, position);
            SendPrivateChatMessage(rpcData.m_senderPeerID, position, userInfo, response);
            return RoutedRpcAction.Consume;
        }

        private static bool TryReadChatMessage(
            ZRoutedRpc.RoutedRPCData rpcData,
            out Vector3 position,
            out UserInfo userInfo,
            out string text)
        {
            position = Vector3.zero;
            userInfo = new UserInfo();
            text = "";
            int originalPosition = rpcData.m_parameters.GetPos();
            try
            {
                rpcData.m_parameters.SetPos(0);
                position = rpcData.m_parameters.ReadVector3();
                rpcData.m_parameters.ReadInt();
                userInfo.Deserialize(ref rpcData.m_parameters);
                text = rpcData.m_parameters.ReadString();
                return true;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning("Failed to parse chat message for server sign command: " + ex.Message);
                return false;
            }
            finally
            {
                rpcData.m_parameters.SetPos(originalPosition);
            }
        }

        private static bool TryReadSayMessage(
            ZRoutedRpc.RoutedRPCData rpcData,
            out UserInfo userInfo,
            out string text)
        {
            userInfo = new UserInfo();
            text = "";
            int originalPosition = rpcData.m_parameters.GetPos();
            try
            {
                rpcData.m_parameters.SetPos(0);
                rpcData.m_parameters.ReadInt();
                userInfo.Deserialize(ref rpcData.m_parameters);
                text = rpcData.m_parameters.ReadString();
                return true;
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning("Failed to parse say message for server sign command: " + ex.Message);
                return false;
            }
            finally
            {
                rpcData.m_parameters.SetPos(originalPosition);
            }
        }

        private static bool IsSignCommand(string text)
        {
            string command = GetSignCommand();
            string trimmed = (text ?? "").Trim();
            return trimmed.Equals(command, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase);
        }

        private static string HandlePlayerSignCommand(long senderPeerId, Vector3 chatPosition)
        {
            if (!ModConfig.EnableServerSigns.Value)
            {
                return "Server signs are disabled on this server.";
            }

            if (!IsServerReady())
            {
                return "Server signs are not ready yet. Try again after the world finishes loading.";
            }

            ZNetPeer? peer = ZNet.instance.GetPeer(senderPeerId);
            if (peer == null || !peer.IsReady())
            {
                return "Could not find your player session for sign registration.";
            }

            float now = Time.realtimeSinceStartup;
            if (NextPlayerSignCommandTimes.TryGetValue(senderPeerId, out float nextAllowedTime) && now < nextAllowedTime)
            {
                float remaining = Mathf.Ceil(nextAllowedTime - now);
                return "Please wait " + remaining.ToString("0", CultureInfo.InvariantCulture) + "s before running " + GetSignCommand() + " again.";
            }

            float cooldown = Mathf.Max(0.0f, ModConfig.ServerSignCommandCooldownSeconds.Value);
            NextPlayerSignCommandTimes[senderPeerId] = now + cooldown;

            Vector3 scanCenter = peer.GetRefPos();
            if (scanCenter == Vector3.zero && chatPosition != Vector3.zero)
            {
                scanCenter = chatPosition;
            }

            SignRegistrationResult result = ScanAndUpdateSignsNear(scanCenter, Mathf.Max(1.0f, ModConfig.ServerSignScanRadius.Value));
            if (result.TotalRegistered > 0)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(
                    peer.m_playerName + " registered or refreshed " + result.TotalRegistered +
                    " sign(s) with " + GetSignCommand() +
                    " near " + FormatVector(scanCenter) + ".");
                return "Registered or refreshed " + result.TotalRegistered + " nearby sign(s).";
            }

            if (result.ScannedSigns == 0)
            {
                return "No nearby signs found. Place a sign with a supported sign command, then run " + GetSignCommand() + ".";
            }

            return "No supported sign commands found nearby. Edit a sign to !reset, then run " + GetSignCommand() + ".";
        }

        private static void SendPrivateChatMessage(long targetPeerId, Vector3 position, UserInfo senderUserInfo, string text)
        {
            if (ZRoutedRpc.instance == null)
            {
                return;
            }

            senderUserInfo.Name = ServerAuthorDisplayName;

            ZRoutedRpc.instance.InvokeRoutedRPC(
                targetPeerId,
                "ChatMessage",
                position,
                (int)Talker.Type.Normal,
                senderUserInfo,
                text);
        }

        private static string GetSignCommand()
        {
            string configured = ModConfig.ServerSignCommand.Value?.Trim() ?? "";
            return configured.Length > 0 ? configured : "!sign";
        }

        private static string ScanAndUpdateSigns(bool force)
        {
            if (!IsServerReady())
            {
                return "Server signs are not ready: server ZDO systems are unavailable.";
            }

            float scanStart = Time.realtimeSinceStartup;
            EnsureRegistryLoaded();

            int dynamicClaimed = 0;
            SignScanBuffer.Clear();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(SignPrefabName, SignScanBuffer, ref index))
            {
            }

            foreach (ZDO sign in SignScanBuffer)
            {
                if (sign == null || !sign.IsValid() || sign.GetPrefab() != SignPrefabHash)
                {
                    continue;
                }

                if (TryRegisterSignFromZdo(sign))
                {
                    dynamicClaimed++;
                }
            }

            int updated = force ? UpdateRegisteredSigns() : 0;

            if (dynamicClaimed > 0 || updated > 0)
            {
                SaveRegistry();
            }

            Metrics.RecordScan(
                SignScanBuffer.Count,
                dynamicClaimed,
                updated,
                (Time.realtimeSinceStartup - scanStart) * 1000.0f);
            DebugLog($"Server sign scan complete. scanned={SignScanBuffer.Count} dynamicClaimed={dynamicClaimed} updated={updated} registered={Signs.Count}.");
            return $"Dynamic claimed {dynamicClaimed}; updated {updated}; registered {Signs.Count}.";
        }

        private static SignRegistrationResult ScanAndUpdateSignsNear(Vector3 center, float radius)
        {
            if (!IsServerReady())
            {
                return SignRegistrationResult.Empty;
            }

            float scanStart = Time.realtimeSinceStartup;
            EnsureRegistryLoaded();

            int dynamicClaimed = 0;
            int nearbySigns = 0;
            float radiusSquared = radius * radius;
            SignScanBuffer.Clear();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(SignPrefabName, SignScanBuffer, ref index))
            {
            }

            foreach (ZDO sign in SignScanBuffer)
            {
                if (sign == null || !sign.IsValid() || sign.GetPrefab() != SignPrefabHash)
                {
                    continue;
                }

                if ((sign.GetPosition() - center).sqrMagnitude > radiusSquared)
                {
                    continue;
                }

                nearbySigns++;
                if (TryRegisterSignFromZdo(sign))
                {
                    dynamicClaimed++;
                }
            }

            int registered = dynamicClaimed;
            if (registered > 0)
            {
                SaveRegistry();
            }

            Metrics.RecordScan(
                SignScanBuffer.Count,
                dynamicClaimed,
                0,
                (Time.realtimeSinceStartup - scanStart) * 1000.0f);
            DebugLog(
                "Nearby server sign scan complete. center=" + FormatVector(center) +
                " radius=" + radius.ToString("0.#", CultureInfo.InvariantCulture) +
                " scanned=" + SignScanBuffer.Count +
                " nearby=" + nearbySigns +
                " dynamicClaimed=" + dynamicClaimed +
                " registered=" + Signs.Count + ".");
            return new SignRegistrationResult(nearbySigns, registered);
        }

        private static bool TryRegisterSignFromZdo(ZDO sign)
        {
            string currentText = sign.GetString(ZDOVars.s_text, "");
            if (TryParseDynamicSignCommand(currentText, out DynamicSignCommand dynamicCommand))
            {
                RegisterSign(sign, dynamicCommand.Source);
                if (!TryWriteDynamicSignText(sign, dynamicCommand))
                {
                    WriteLoadingText(sign, dynamicCommand);
                }

                MaybeRefreshResetSign(dynamicCommand, force: false, resetDataChanged: false);
                return true;
            }

            return false;
        }

        private static int UpdateRegisteredSigns()
        {
            if (!IsServerReady())
            {
                return 0;
            }

            EnsureRegistryLoaded();

            int updated = 0;
            List<ZDOID> missing = new();
            foreach (ZDOID id in Signs.Keys.ToList())
            {
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null)
                {
                    continue;
                }

                if (!zdo.IsValid() || zdo.GetPrefab() != SignPrefabHash)
                {
                    missing.Add(id);
                    continue;
                }

                if (TryGetDynamicSignCommand(Signs[id], out DynamicSignCommand dynamicCommand))
                {
                    MaybeRefreshResetSign(dynamicCommand, force: false, resetDataChanged: false);
                    if (TryWriteDynamicSignText(zdo, dynamicCommand))
                    {
                        updated++;
                    }
                }
                else
                {
                    missing.Add(id);
                }
            }

            foreach (ZDOID id in missing)
            {
                Signs.Remove(id);
            }

            if (missing.Count > 0 || updated > 0)
            {
                SaveRegistry();
            }

            return updated;
        }

        private static int UpdateDynamicSigns(string source)
        {
            if (!IsServerReady())
            {
                return 0;
            }

            EnsureRegistryLoaded();
            int updated = 0;
            List<ZDOID> missing = new();
            foreach (ServerSignRegistration sign in Signs.Values.ToList())
            {
                if (!string.Equals(sign.Source, source, StringComparison.OrdinalIgnoreCase) ||
                    !TryGetDynamicSignCommand(sign, out DynamicSignCommand command))
                {
                    continue;
                }

                ZDO zdo = ZDOMan.instance.GetZDO(sign.ZdoId);
                if (zdo == null)
                {
                    continue;
                }

                if (!zdo.IsValid() || zdo.GetPrefab() != SignPrefabHash)
                {
                    missing.Add(sign.ZdoId);
                    continue;
                }

                if (TryWriteDynamicSignText(zdo, command))
                {
                    updated++;
                }
            }

            foreach (ZDOID id in missing)
            {
                Signs.Remove(id);
            }

            if (missing.Count > 0)
            {
                SaveRegistry();
            }

            return updated;
        }

        private static string CreateDynamicSignExamples(string? playerName)
        {
            if (!IsServerReady())
            {
                return "Server signs are not ready: server ZDO systems are unavailable.";
            }

            ZNetPeer? peer = FindTargetPeer(playerName);
            if (peer == null)
            {
                return string.IsNullOrWhiteSpace(playerName)
                    ? "No connected player found for sign examples."
                    : $"No connected player named {playerName} found.";
            }

            GameObject signPrefab = ZNetScene.instance.GetPrefab(SignPrefabName);
            GameObject supportPrefab = ZNetScene.instance.GetPrefab(SupportPrefabName);
            if (signPrefab == null || supportPrefab == null)
            {
                return "Could not find Valheim sign or wood_pole prefab.";
            }

            Vector3 center = peer.GetRefPos();
            Vector3[] offsets =
            {
                new(-2.4f, 0.0f, 3.2f),
                new(-0.8f, 0.0f, 3.2f),
                new(0.8f, 0.0f, 3.2f),
                new(2.4f, 0.0f, 3.2f),
            };
            string[] claims =
            {
                "!reset size=1.1 alignment=center",
                "!reset reset=location biome=ashlands size=10",
                "!reset reset=dungeon biome=ashlands",
                "!reset reset=vegetation vegetation=copper size=0.8",
            };

            int created = 0;
            int supportsCreated = 0;
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 anchor = center + offsets[i];
                GameObject support = Object.Instantiate(supportPrefab, anchor + new Vector3(0.0f, 0.5f, 0.0f), Quaternion.identity);
                ZNetView supportView = support.GetComponent<ZNetView>();
                ZDO? supportZdo = supportView != null ? supportView.GetZDO() : null;
                if (supportZdo != null)
                {
                    supportZdo.SetOwner(ZDOMan.GetSessionID());
                    ZDOMan.instance.ForceSendZDO(supportZdo.m_uid);
                    supportsCreated++;
                }
                else
                {
                    Object.Destroy(support);
                }

                Vector3 lookDirection = center - anchor;
                Quaternion rotation = lookDirection.sqrMagnitude > 0.01f
                    ? Quaternion.LookRotation(lookDirection.normalized, Vector3.up)
                    : Quaternion.identity;
                GameObject instance = Object.Instantiate(signPrefab, anchor + new Vector3(0.0f, 1.35f, 0.0f), rotation);
                ZNetView nview = instance.GetComponent<ZNetView>();
                ZDO? zdo = nview != null ? nview.GetZDO() : null;
                if (zdo == null)
                {
                    Object.Destroy(instance);
                    continue;
                }

                zdo.SetOwner(ZDOMan.GetSessionID());
                zdo.Set(ZDOVars.s_text, claims[i]);
                zdo.Set(ZDOVars.s_author, "");
                zdo.Set(ZDOVars.s_authorDisplayName, ServerAuthorDisplayName);
                ZDOMan.instance.ForceSendZDO(zdo.m_uid);
                created++;
            }

            return $"Created {created} reset sign command examples and {supportsCreated} wood_pole supports near {peer.m_playerName}. Stand nearby and run {GetSignCommand()}, or run sst_signs_scan from the console.";
        }

        private static void RegisterSign(ZDO sign, string source)
        {
            ServerSignRegistration registration = new(
                sign.m_uid,
                source,
                sign.GetPosition(),
                DateTimeOffset.UtcNow);
            Signs[sign.m_uid] = registration;
        }

        private static bool TryWriteDynamicSignText(ZDO sign, DynamicSignCommand command)
        {
            if (string.Equals(command.Kind, "reset", StringComparison.OrdinalIgnoreCase) &&
                ResetDataFile.TryBuildSignText(command.Variant, command.Subject, command.Size, command.Alignment, out string resetText))
            {
                QueueSignText(sign.m_uid, resetText, command.Source);
                return true;
            }

            return false;
        }

        private static void WriteLoadingText(ZDO sign, DynamicSignCommand command)
        {
            QueueSignText(
                sign.m_uid,
                "<align=\"" + command.Alignment + "\"><nobr><size=1.2><color=#ffd166><b>Loading</b></color></size></nobr></align>\n" +
                "<align=\"" + command.Alignment + "\"><nobr><color=#ffffff>" + EscapeRichText(command.DisplayName) + "</color></nobr></align>",
                command.Source);
        }

        private static void QueueSignText(ZDOID zdoId, string text, string source)
        {
            string clamped = ClampText(text);
            if (!PendingSignWrites.ContainsKey(zdoId))
            {
                PendingSignWriteOrder.Enqueue(zdoId);
            }

            PendingSignWrites[zdoId] = new PendingSignWrite(zdoId, clamped, source);
            Metrics.WritesQueued++;
        }

        private static void ProcessPendingSignWrites()
        {
            if (PendingSignWrites.Count == 0)
            {
                return;
            }

            int maxWrites = Mathf.Max(1, ModConfig.ServerSignMaxWritesPerUpdate.Value);
            int processed = 0;
            float start = Time.realtimeSinceStartup;
            while (processed < maxWrites && PendingSignWriteOrder.Count > 0)
            {
                ZDOID zdoId = PendingSignWriteOrder.Dequeue();
                if (!PendingSignWrites.TryGetValue(zdoId, out PendingSignWrite pending))
                {
                    continue;
                }

                PendingSignWrites.Remove(zdoId);
                processed++;

                ZDO zdo = ZDOMan.instance.GetZDO(zdoId);
                if (zdo == null || !zdo.IsValid() || zdo.GetPrefab() != SignPrefabHash)
                {
                    Metrics.WritesMissing++;
                    continue;
                }

                ApplyQueuedSignText(zdo, pending);
            }

            Metrics.WriteProcessMs += (Time.realtimeSinceStartup - start) * 1000.0f;
        }

        private static void ApplyQueuedSignText(ZDO sign, PendingSignWrite pending)
        {
            string currentText = sign.GetString(ZDOVars.s_text, "");
            string currentAuthor = sign.GetString(ZDOVars.s_author, "");
            string currentDisplayName = sign.GetString(ZDOVars.s_authorDisplayName, "");
            if (string.Equals(currentText, pending.Text, StringComparison.Ordinal) &&
                string.Equals(currentAuthor, "", StringComparison.Ordinal) &&
                string.Equals(currentDisplayName, ServerAuthorDisplayName, StringComparison.Ordinal))
            {
                Metrics.WritesSkipped++;
                return;
            }

            sign.SetOwner(ZDOMan.GetSessionID());
            sign.Set(ZDOVars.s_text, pending.Text);
            sign.Set(ZDOVars.s_author, "");
            sign.Set(ZDOVars.s_authorDisplayName, ServerAuthorDisplayName);
            ZDOMan.instance.ForceSendZDO(sign.m_uid);
            Metrics.WritesDone++;
        }

        private static void MaybeRefreshResetSign(DynamicSignCommand command, bool force, bool resetDataChanged)
        {
            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Max(10.0f, ModConfig.ServerSignResetSignRefreshSeconds.Value);
            if (!force &&
                !resetDataChanged &&
                DynamicSignNextRefreshTimes.TryGetValue(command.Source, out float nextRefreshTime) &&
                now < nextRefreshTime)
            {
                Metrics.CacheHits++;
                return;
            }

            DynamicSignNextRefreshTimes[command.Source] = now + interval;
            int queued = UpdateDynamicSigns(command.Source);
            DebugLog("Queued " + queued + " reset sign refresh(es) for " + command.DisplayName + ".");
        }

        private static void RefreshRegisteredSigns(bool resetDataChanged)
        {
            EnsureRegistryLoaded();
            HashSet<string> refreshedSources = new(StringComparer.OrdinalIgnoreCase);
            List<ZDOID> unsupported = new();
            foreach (ServerSignRegistration sign in Signs.Values.ToList())
            {
                if (TryGetDynamicSignCommand(sign, out DynamicSignCommand command))
                {
                    if (!refreshedSources.Add(command.Source))
                    {
                        Metrics.CacheHits++;
                        continue;
                    }

                    MaybeRefreshResetSign(command, force: false, resetDataChanged);

                    continue;
                }

                if (DynamicSignCommand.TryParseSource(sign.Source, out _))
                {
                    unsupported.Add(sign.ZdoId);
                }
            }

            foreach (ZDOID id in unsupported)
            {
                Signs.Remove(id);
            }

            if (unsupported.Count > 0)
            {
                SaveRegistry();
            }
        }

        private static string ClampText(string rendered)
        {
            int maxCharacters = Mathf.Max(200, ModConfig.ServerSignMaxCharacters.Value);
            return rendered.Length <= maxCharacters
                ? rendered
                : rendered.Substring(0, maxCharacters);
        }

        private static string EscapeRichText(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static bool TryParseDynamicSignCommand(string currentText, out DynamicSignCommand command)
        {
            command = DynamicSignCommand.Empty;
            List<string> tokens = TokenizeCommand(currentText);
            if (tokens.Count == 0)
            {
                return false;
            }

            string verb = tokens[0].Trim().ToLowerInvariant();
            if (verb != "!reset")
            {
                return false;
            }

            if (!TryParseCommandParameters(tokens.Skip(1), out Dictionary<string, string> parameters) ||
                !TryReadSize(parameters, out string scale) ||
                !TryReadAlignment(parameters, out string alignment))
            {
                return false;
            }

            string resetName = "summary";
            string resetFilter = "";
            if (TryTakeParameter(parameters, "reset", out string rawReset))
            {
                resetName = rawReset.Trim();
            }

            if (string.Equals(resetName, "location", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resetName, "locations", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resetName, "dungeon", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resetName, "dungeons", StringComparison.OrdinalIgnoreCase))
            {
                if (TryTakeParameter(parameters, "biome", out string rawBiome))
                {
                    resetFilter = "biome=" + rawBiome.Trim();
                }
            }
            else if (string.Equals(resetName, "vegetation", StringComparison.OrdinalIgnoreCase))
            {
                if (TryTakeParameter(parameters, "vegetation", out string rawVegetation))
                {
                    resetFilter = "vegetation=" + rawVegetation.Trim();
                }
            }
            else if (parameters.ContainsKey("biome") || parameters.ContainsKey("vegetation"))
            {
                return false;
            }

            if (parameters.Count > 0 || string.IsNullOrWhiteSpace(resetName))
            {
                return false;
            }

            command = DynamicSignCommand.Create("reset", resetName, resetFilter, scale, alignment);
            return true;
        }

        private static bool TryGetDynamicSignCommand(ServerSignRegistration sign, out DynamicSignCommand command)
        {
            return DynamicSignCommand.TryParseSource(sign.Source, out command) &&
                IsSupportedDynamicSignCommand(command);
        }

        private static bool IsSupportedDynamicSignCommand(DynamicSignCommand command)
        {
            return string.Equals(command.Kind, "reset", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> TokenizeCommand(string value)
        {
            List<string> tokens = new();
            StringBuilder current = new();
            bool inQuote = false;
            foreach (char c in value.Trim())
            {
                if (c == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }

                if (char.IsWhiteSpace(c) && !inQuote)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens;
        }

        private static bool TryParseCommandParameters(IEnumerable<string> tokens, out Dictionary<string, string> parameters)
        {
            parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in tokens)
            {
                string normalized = token.Trim();
                int separator = normalized.IndexOf("=", StringComparison.Ordinal);
                if (separator <= 0 || separator >= normalized.Length - 1)
                {
                    return false;
                }

                string key = normalized.Substring(0, separator).Trim().ToLowerInvariant();
                string value = normalized.Substring(separator + 1).Trim();
                if (key.Length == 0 || value.Length == 0 || parameters.ContainsKey(key))
                {
                    return false;
                }

                parameters[key] = value;
            }

            return true;
        }

        private static bool TryReadSize(Dictionary<string, string> parameters, out string scale)
        {
            scale = SignTextScale.DefaultSourceValue;
            if (!TryTakeParameter(parameters, "size", out string rawSize))
            {
                return true;
            }

            return SignTextScale.TryNormalize(rawSize, out scale);
        }

        private static bool TryReadAlignment(Dictionary<string, string> parameters, out string alignment)
        {
            alignment = SignTextAlignment.DefaultSourceValue;
            if (!TryTakeParameter(parameters, "alignment", out string rawAlignment))
            {
                return true;
            }

            return SignTextAlignment.TryNormalize(rawAlignment, out alignment);
        }

        private static bool TryTakeParameter(Dictionary<string, string> parameters, string key, out string value)
        {
            if (!parameters.TryGetValue(key, out value))
            {
                value = "";
                return false;
            }

            parameters.Remove(key);
            return true;
        }

        private static bool IsEnabled()
        {
            return ModConfig.EnableServerSigns.Value && IsServerReady();
        }

        private static bool IsServerReady()
        {
            return ZNet.instance != null &&
                ZNet.instance.IsServer() &&
                ZDOMan.instance != null &&
                ZNetScene.instance != null;
        }

        private static ZNetPeer? FindTargetPeer(string? playerName)
        {
            if (ZNet.instance == null)
            {
                return null;
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (string.IsNullOrWhiteSpace(playerName))
            {
                return peers.FirstOrDefault(peer => peer != null && peer.IsReady());
            }

            return peers.FirstOrDefault(peer =>
                peer != null &&
                peer.IsReady() &&
                string.Equals(peer.m_playerName, playerName, StringComparison.OrdinalIgnoreCase));
        }

        private static void EnsureRegistryLoaded()
        {
            if (_registryLoaded)
            {
                return;
            }

            _registryLoaded = true;
            Signs.Clear();
            string path = ResolveRegistryPath();
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                ServerSignRegistryFile registry = ReadRegistryFile(path);
                foreach (ServerSignRegistryEntry entry in registry.signs ?? new List<ServerSignRegistryEntry>())
                {
                    if (entry == null ||
                        string.IsNullOrWhiteSpace(entry.source) ||
                        !long.TryParse(entry.userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long userId) ||
                        !uint.TryParse(entry.id, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id) ||
                        !DateTimeOffset.TryParse(
                            entry.createdAtUtc,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out DateTimeOffset createdAt))
                    {
                        continue;
                    }

                    ZDOID zdoId = new(userId, id);
                    Signs[zdoId] = new ServerSignRegistration(
                        zdoId,
                        entry.source,
                        new Vector3(entry.x, entry.y, entry.z),
                        createdAt);
                }
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning("Failed to read server sign registry JSON: " + ex.Message);
                Signs.Clear();
            }

            DebugLog($"Loaded {Signs.Count} server sign registration(s).");
        }

        private static void SaveRegistry()
        {
            string path = ResolveRegistryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Paths.ConfigPath);
            ServerSignRegistryFile registry = new();
            registry.signs = Signs.Values
                .OrderBy(sign => sign.ZdoId.UserID)
                .ThenBy(sign => sign.ZdoId.ID)
                .Select(sign => new ServerSignRegistryEntry
                {
                    userId = sign.ZdoId.UserID.ToString(CultureInfo.InvariantCulture),
                    id = sign.ZdoId.ID.ToString(CultureInfo.InvariantCulture),
                    source = sign.Source,
                    x = sign.Position.x,
                    y = sign.Position.y,
                    z = sign.Position.z,
                    createdAtUtc = sign.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                })
                .ToList();
            WriteRegistryFile(path, registry);
        }

        private static ServerSignRegistryFile ReadRegistryFile(string path)
        {
            return JsonConvert.DeserializeObject<ServerSignRegistryFile>(File.ReadAllText(path)) ?? new ServerSignRegistryFile();
        }

        private static void WriteRegistryFile(string path, ServerSignRegistryFile registry)
        {
            string json = JsonConvert.SerializeObject(registry, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        private static string ResolveRegistryPath()
        {
            string configured = ModConfig.ServerSignRegistryFile.Value?.Trim() ?? "";
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Paths.ConfigPath, configured.Length > 0 ? configured : DefaultRegistryFileName);
        }

        private static string FormatVector(Vector3 value)
        {
            return value.x.ToString("0.#", CultureInfo.InvariantCulture) + "," +
                value.y.ToString("0.#", CultureInfo.InvariantCulture) + "," +
                value.z.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugServerSigns.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private static void MaybeLogMetrics()
        {
            if (!ModConfig.ServerSignLogMetrics.Value)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Max(30.0f, ModConfig.ServerSignMetricsLogIntervalSeconds.Value);
            if (now < _nextMetricsLogTime)
            {
                return;
            }

            _nextMetricsLogTime = now + interval;
            ServerSideTweaksPlugin.ModLogger.LogInfo(
                "Server signs metrics: " +
                "registered=" + Signs.Count +
                " queued=" + PendingSignWrites.Count +
                " scans=" + Metrics.Scans +
                " scannedSigns=" + Metrics.ScannedSigns +
                " dynamicClaimed=" + Metrics.DynamicClaimed +
                " scanMs=" + Metrics.ScanMs.ToString("0.##", CultureInfo.InvariantCulture) +
                " resetChecks=" + Metrics.ResetFileChecks +
                " resetChanged=" + Metrics.ResetFileChanged +
                " resetReadMs=" + Metrics.ResetFileReadMs.ToString("0.##", CultureInfo.InvariantCulture) +
                " cacheHits=" + Metrics.CacheHits +
                " writesQueued=" + Metrics.WritesQueued +
                " writesDone=" + Metrics.WritesDone +
                " writesSkipped=" + Metrics.WritesSkipped +
                " writesMissing=" + Metrics.WritesMissing +
                " writeMs=" + Metrics.WriteProcessMs.ToString("0.##", CultureInfo.InvariantCulture));
            Metrics.Clear();
        }

        private sealed class ServerSignRegistration
        {
            internal ServerSignRegistration(ZDOID zdoId, string source, Vector3 position, DateTimeOffset createdAt)
            {
                ZdoId = zdoId;
                Source = source;
                Position = position;
                CreatedAt = createdAt;
            }

            internal ZDOID ZdoId { get; }
            internal string Source { get; }
            internal Vector3 Position { get; }
            internal DateTimeOffset CreatedAt { get; }
        }

        [Serializable]
        private sealed class ServerSignRegistryFile
        {
            public List<ServerSignRegistryEntry> signs = new();
        }

        [Serializable]
        private sealed class ServerSignRegistryEntry
        {
            public string userId = "";
            public string id = "";
            public string source = "";
            public float x;
            public float y;
            public float z;
            public string createdAtUtc = "";
        }

        private readonly struct SignRegistrationResult
        {
            internal static readonly SignRegistrationResult Empty = new(0, 0);

            internal SignRegistrationResult(int scannedSigns, int totalRegistered)
            {
                ScannedSigns = scannedSigns;
                TotalRegistered = totalRegistered;
            }

            internal int ScannedSigns { get; }
            internal int TotalRegistered { get; }
        }

        private sealed class PendingSignWrite
        {
            internal PendingSignWrite(ZDOID zdoId, string text, string source)
            {
                ZdoId = zdoId;
                Text = text;
                Source = source;
            }

            internal ZDOID ZdoId { get; }
            internal string Text { get; }
            internal string Source { get; }
        }

        private sealed class ServerSignMetrics
        {
            internal long Scans;
            internal long ScannedSigns;
            internal long DynamicClaimed;
            internal long RegisteredUpdated;
            internal float ScanMs;
            internal long ResetFileChecks;
            internal long ResetFileChanged;
            internal float ResetFileReadMs;
            internal long CacheHits;
            internal long WritesQueued;
            internal long WritesDone;
            internal long WritesSkipped;
            internal long WritesMissing;
            internal float WriteProcessMs;

            internal void RecordScan(
                int scannedSigns,
                int dynamicClaimed,
                int registeredUpdated,
                float scanMs)
            {
                Scans++;
                ScannedSigns += scannedSigns;
                DynamicClaimed += dynamicClaimed;
                RegisteredUpdated += registeredUpdated;
                ScanMs += scanMs;
            }

            internal void RecordResetFileCheck(float readMs, bool changed)
            {
                ResetFileChecks++;
                ResetFileReadMs += readMs;
                if (changed)
                {
                    ResetFileChanged++;
                }
            }

            internal void Clear()
            {
                Scans = 0;
                ScannedSigns = 0;
                DynamicClaimed = 0;
                RegisteredUpdated = 0;
                ScanMs = 0.0f;
                ResetFileChecks = 0;
                ResetFileChanged = 0;
                ResetFileReadMs = 0.0f;
                CacheHits = 0;
                WritesQueued = 0;
                WritesDone = 0;
                WritesSkipped = 0;
                WritesMissing = 0;
                WriteProcessMs = 0.0f;
            }
        }

        private sealed class DynamicSignCommand
        {
            private DynamicSignCommand(string kind, string variant, string subject, string size, string alignment)
            {
                Kind = kind;
                Variant = variant;
                Subject = subject;
                Size = size;
                Alignment = alignment;
                Source = string.Join(
                    ":",
                    DynamicSource,
                    Uri.EscapeDataString(Kind),
                    Uri.EscapeDataString(Variant),
                    Uri.EscapeDataString(Subject),
                    Uri.EscapeDataString(Size),
                    Uri.EscapeDataString(Alignment));
                DisplayName = BuildDisplayName(kind, variant, subject);
            }

            internal static DynamicSignCommand Empty { get; } = new("", "", "", SignTextScale.DefaultSourceValue, SignTextAlignment.DefaultSourceValue);

            internal string Kind { get; }
            internal string Variant { get; }
            internal string Subject { get; }
            internal string Size { get; }
            internal string Alignment { get; }
            internal string Source { get; }
            internal string DisplayName { get; }

            internal static DynamicSignCommand Create(string kind, string variant, string subject, string size, string alignment)
            {
                string normalizedSize = string.IsNullOrWhiteSpace(size)
                    ? SignTextScale.DefaultSourceValue
                    : SignTextScale.NormalizeOrDefault(size);
                string normalizedAlignment = string.IsNullOrWhiteSpace(alignment)
                    ? SignTextAlignment.DefaultSourceValue
                    : SignTextAlignment.NormalizeOrDefault(alignment);
                return new(
                    kind.Trim().ToLowerInvariant(),
                    variant.Trim().ToLowerInvariant(),
                    subject.Trim(),
                    normalizedSize,
                    normalizedAlignment);
            }

            internal static bool TryParseSource(string source, out DynamicSignCommand command)
            {
                command = Empty;
                if (!source.StartsWith(DynamicSource + ":", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string[] parts = source.Split(':');
                if (parts.Length < 5)
                {
                    return false;
                }

                command = Create(
                    Uri.UnescapeDataString(parts[1]),
                    Uri.UnescapeDataString(parts[2]),
                    Uri.UnescapeDataString(parts[3]),
                    Uri.UnescapeDataString(parts[4]),
                    parts.Length >= 6
                        ? Uri.UnescapeDataString(parts[5])
                        : SignTextAlignment.DefaultSourceValue);
                return true;
            }

            private static string BuildDisplayName(string kind, string variant, string subject)
            {
                if (string.Equals(kind, "reset", StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(subject)
                        ? "reset " + variant
                        : "reset " + variant + " " + subject;
                }

                return kind;
            }
        }
    }
}
