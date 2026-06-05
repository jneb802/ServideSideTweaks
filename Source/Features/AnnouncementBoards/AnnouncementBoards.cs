using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using ServerSideTweaks.Infrastructure.Routing;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace ServerSideTweaks.Features.AnnouncementBoards
{
    internal static class AnnouncementBoards
    {
        private const string SignPrefabName = "sign";
        private const string SupportPrefabName = "wood_pole";
        private const string ServerAuthorDisplayName = "Server";
        private const string LeaderboardCommand = "!leaderboard";
        private const string DynamicSource = "dynamic";
        private const string LeaderboardDeaths = "deaths";
        private const string PlayerSummary = "summary";
        private static readonly int SignPrefabHash = SignPrefabName.GetStableHashCode();
        private static readonly Dictionary<ZDOID, BoardRegistration> Boards = new();
        private static readonly Dictionary<string, DynamicSignCache> DynamicSignCaches = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> DynamicSignNextFetchTimes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DynamicSignFetchesInFlight = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<ZDO> SignScanBuffer = new();
        private static readonly Queue<ZDOID> PendingSignWriteOrder = new();
        private static readonly Dictionary<ZDOID, PendingSignWrite> PendingSignWrites = new();
        private static readonly Dictionary<long, float> NextPlayerSignCommandTimes = new();
        private static readonly BoardMetrics Metrics = new();
        private static float _nextUpdateTime;
        private static float _nextLeaderboardWarningTime;
        private static float _nextMetricsLogTime;
        private static bool _registryLoaded;

        internal static void ClearRuntimeCache()
        {
            Boards.Clear();
            SignScanBuffer.Clear();
            _nextUpdateTime = 0.0f;
            _nextLeaderboardWarningTime = 0.0f;
            _registryLoaded = false;
            DynamicSignCaches.Clear();
            DynamicSignNextFetchTimes.Clear();
            DynamicSignFetchesInFlight.Clear();
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
                "sst_boards_scan",
                "Scans supported announcement board sign commands.",
                args => ScanAndUpdateBoards(force: true),
                onlyAdmin: true,
                remoteCommand: true);

            new Terminal.ConsoleCommand(
                "sst_boards_leaderboard_example",
                "Creates a supported !leaderboard board=deaths sign near a connected player.",
                args => CreateLeaderboardExampleBoard(args.Length >= 2 ? args[1] : null),
                onlyAdmin: true,
                remoteCommand: true);

            new Terminal.ConsoleCommand(
                "sst_boards_sign_examples",
                "Creates supported !leaderboard board=deaths, !player, and !reset signs near a connected player.",
                args => CreateDynamicSignExampleBoards(
                    args.Length >= 2 ? args[1] : null,
                    args.Length >= 3 ? args[2] : null),
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

            _nextUpdateTime = Time.time + Mathf.Max(1.0f, ModConfig.AnnouncementBoardUpdateIntervalSeconds.Value);
            float resetReadStart = Time.realtimeSinceStartup;
            bool resetDataChanged = ResetDataFile.Update();
            Metrics.RecordResetFileCheck((Time.realtimeSinceStartup - resetReadStart) * 1000.0f, resetDataChanged);
            RefreshRegisteredBoards(resetDataChanged);
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
                ServerSideTweaksPlugin.ModLogger.LogWarning("Failed to parse chat message for announcement board command: " + ex.Message);
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
                ServerSideTweaksPlugin.ModLogger.LogWarning("Failed to parse say message for announcement board command: " + ex.Message);
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
            if (!ModConfig.EnableAnnouncementBoards.Value)
            {
                return "Sign boards are disabled on this server.";
            }

            if (!IsServerReady())
            {
                return "Sign boards are not ready yet. Try again after the world finishes loading.";
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

            float cooldown = Mathf.Max(0.0f, ModConfig.AnnouncementBoardSignCommandCooldownSeconds.Value);
            NextPlayerSignCommandTimes[senderPeerId] = now + cooldown;

            Vector3 scanCenter = peer.GetRefPos();
            if (scanCenter == Vector3.zero && chatPosition != Vector3.zero)
            {
                scanCenter = chatPosition;
            }

            SignRegistrationResult result = ScanAndUpdateBoardsNear(scanCenter, Mathf.Max(1.0f, ModConfig.AnnouncementBoardSignScanRadius.Value));
            if (result.TotalRegistered > 0)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(
                    peer.m_playerName + " registered or refreshed " + result.TotalRegistered +
                    " sign board(s) with " + GetSignCommand() +
                    " near " + FormatVector(scanCenter) + ".");
                return "Registered or refreshed " + result.TotalRegistered + " nearby sign board(s).";
            }

            if (result.ScannedSigns == 0)
            {
                return "No nearby signs found. Place a sign with a supported board command, then run " + GetSignCommand() + ".";
            }

            return "No supported sign commands found nearby. Edit a sign to !leaderboard board=deaths, !player player=<name>, or !reset, then run " + GetSignCommand() + ".";
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
            string configured = ModConfig.AnnouncementBoardSignCommand.Value?.Trim() ?? "";
            return configured.Length > 0 ? configured : "!sign";
        }

        private static string ScanAndUpdateBoards(bool force)
        {
            if (!IsServerReady())
            {
                return "Announcement boards are not ready: server ZDO systems are unavailable.";
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

                if (TryRegisterBoardFromSign(sign))
                {
                    dynamicClaimed++;
                }
            }

            int updated = force ? UpdateRegisteredBoards() : 0;

            if (dynamicClaimed > 0 || updated > 0)
            {
                SaveRegistry();
            }

            Metrics.RecordScan(
                SignScanBuffer.Count,
                dynamicClaimed,
                updated,
                (Time.realtimeSinceStartup - scanStart) * 1000.0f);
            DebugLog($"Announcement board scan complete. scanned={SignScanBuffer.Count} dynamicClaimed={dynamicClaimed} updated={updated} registered={Boards.Count}.");
            return $"Dynamic claimed {dynamicClaimed}; updated {updated}; registered {Boards.Count}.";
        }

        private static SignRegistrationResult ScanAndUpdateBoardsNear(Vector3 center, float radius)
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
                if (TryRegisterBoardFromSign(sign))
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
                "Nearby announcement board scan complete. center=" + FormatVector(center) +
                " radius=" + radius.ToString("0.#", CultureInfo.InvariantCulture) +
                " scanned=" + SignScanBuffer.Count +
                " nearby=" + nearbySigns +
                " dynamicClaimed=" + dynamicClaimed +
                " registered=" + Boards.Count + ".");
            return new SignRegistrationResult(nearbySigns, registered);
        }

        private static bool TryRegisterBoardFromSign(ZDO sign)
        {
            string currentText = sign.GetString(ZDOVars.s_text, "");
            if (TryParseDynamicSignCommand(currentText, out DynamicSignCommand dynamicCommand))
            {
                RegisterBoard(sign, dynamicCommand.Source);
                if (!TryWriteDynamicSignText(sign, dynamicCommand))
                {
                    WriteLoadingText(sign, dynamicCommand);
                }

                MaybeFetchDynamicSign(dynamicCommand, force: false);
                return true;
            }

            return false;
        }

        private static int UpdateRegisteredBoards()
        {
            if (!IsServerReady())
            {
                return 0;
            }

            EnsureRegistryLoaded();

            int updated = 0;
            List<ZDOID> missing = new();
            foreach (ZDOID id in Boards.Keys.ToList())
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

                if (TryGetDynamicSignCommand(Boards[id], out DynamicSignCommand dynamicCommand))
                {
                    MaybeFetchDynamicSign(dynamicCommand, force: false);
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
                Boards.Remove(id);
            }

            if (missing.Count > 0 || updated > 0)
            {
                SaveRegistry();
            }

            return updated;
        }

        private static int UpdateDynamicSignBoards(string source)
        {
            if (!IsServerReady())
            {
                return 0;
            }

            EnsureRegistryLoaded();
            int updated = 0;
            List<ZDOID> missing = new();
            foreach (BoardRegistration board in Boards.Values.ToList())
            {
                if (!string.Equals(board.Source, source, StringComparison.OrdinalIgnoreCase) ||
                    !TryGetDynamicSignCommand(board, out DynamicSignCommand command))
                {
                    continue;
                }

                ZDO zdo = ZDOMan.instance.GetZDO(board.ZdoId);
                if (zdo == null)
                {
                    continue;
                }

                if (!zdo.IsValid() || zdo.GetPrefab() != SignPrefabHash)
                {
                    missing.Add(board.ZdoId);
                    continue;
                }

                if (TryWriteDynamicSignText(zdo, command))
                {
                    updated++;
                }
            }

            foreach (ZDOID id in missing)
            {
                Boards.Remove(id);
            }

            if (missing.Count > 0)
            {
                SaveRegistry();
            }

            return updated;
        }

        private static string CreateLeaderboardExampleBoard(string? playerName)
        {
            if (!IsServerReady())
            {
                return "Announcement boards are not ready: server ZDO systems are unavailable.";
            }

            ZNetPeer? peer = FindTargetPeer(playerName);
            if (peer == null)
            {
                return string.IsNullOrWhiteSpace(playerName)
                    ? "No connected player found for leaderboard board."
                    : $"No connected player named {playerName} found.";
            }

            GameObject signPrefab = ZNetScene.instance.GetPrefab(SignPrefabName);
            GameObject supportPrefab = ZNetScene.instance.GetPrefab(SupportPrefabName);
            if (signPrefab == null || supportPrefab == null)
            {
                return "Could not find Valheim sign or wood_pole prefab.";
            }

            Vector3 basePosition = peer.GetRefPos() + new Vector3(0.0f, 0.0f, 3.0f);
            GameObject support = Object.Instantiate(supportPrefab, basePosition + new Vector3(0.0f, 0.5f, 0.0f), Quaternion.identity);
            ZNetView supportView = support.GetComponent<ZNetView>();
            ZDO? supportZdo = supportView != null ? supportView.GetZDO() : null;
            if (supportZdo != null)
            {
                supportZdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.ForceSendZDO(supportZdo.m_uid);
            }
            else
            {
                Object.Destroy(support);
            }

            GameObject instance = Object.Instantiate(
                signPrefab,
                basePosition + new Vector3(0.0f, 1.35f, -0.08f),
                Quaternion.Euler(0.0f, 180.0f, 0.0f));
            ZNetView nview = instance.GetComponent<ZNetView>();
            ZDO? zdo = nview != null ? nview.GetZDO() : null;
            if (zdo == null)
            {
                Object.Destroy(instance);
                return "Could not create leaderboard sign ZDO.";
            }

            zdo.SetOwner(ZDOMan.GetSessionID());
            zdo.Set(ZDOVars.s_text, LeaderboardCommand + " board=deaths");
            zdo.Set(ZDOVars.s_author, "");
            zdo.Set(ZDOVars.s_authorDisplayName, ServerAuthorDisplayName);
            ZDOMan.instance.ForceSendZDO(zdo.m_uid);
            return $"Created a supported {LeaderboardCommand} board=deaths sign near {peer.m_playerName}. Stand nearby and run {GetSignCommand()}, or run sst_boards_scan from the console.";
        }

        private static string CreateDynamicSignExampleBoards(string? playerName, string? statsPlayerName)
        {
            if (!IsServerReady())
            {
                return "Announcement boards are not ready: server ZDO systems are unavailable.";
            }

            ZNetPeer? peer = FindTargetPeer(playerName);
            if (peer == null)
            {
                return string.IsNullOrWhiteSpace(playerName)
                    ? "No connected player found for sign example boards."
                    : $"No connected player named {playerName} found.";
            }

            GameObject signPrefab = ZNetScene.instance.GetPrefab(SignPrefabName);
            GameObject supportPrefab = ZNetScene.instance.GetPrefab(SupportPrefabName);
            if (signPrefab == null || supportPrefab == null)
            {
                return "Could not find Valheim sign or wood_pole prefab.";
            }

            string statsPlayer = string.IsNullOrWhiteSpace(statsPlayerName) ? "Taro" : statsPlayerName!.Trim();
            Vector3 center = peer.GetRefPos();
            Vector3[] offsets =
            {
                new(-4.0f, 0.0f, 3.2f),
                new(-2.4f, 0.0f, 3.2f),
                new(-0.8f, 0.0f, 3.2f),
                new(0.8f, 0.0f, 3.2f),
                new(2.4f, 0.0f, 3.2f),
                new(4.0f, 0.0f, 3.2f),
            };
            string[] claims =
            {
                "!leaderboard board=deaths alignment=center",
                "!player player=\"" + statsPlayer + "\"",
                "!player player=\"" + statsPlayer + "\" stat=deaths size=1.1 alignment=right",
                "!player player=\"" + statsPlayer + "\" stat=last-online",
                "!reset size=1.1 alignment=center",
                "!reset reset=copper size=0.8",
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

            return $"Created {created} dynamic sign command examples and {supportsCreated} wood_pole supports near {peer.m_playerName}. Stand nearby and run {GetSignCommand()}, or run sst_boards_scan from the console.";
        }

        private static void RegisterBoard(ZDO sign, string source)
        {
            BoardRegistration registration = new(
                sign.m_uid,
                source,
                sign.GetPosition(),
                DateTimeOffset.UtcNow);
            Boards[sign.m_uid] = registration;
        }

        private static bool TryWriteDynamicSignText(ZDO sign, DynamicSignCommand command)
        {
            if (string.Equals(command.Kind, "reset", StringComparison.OrdinalIgnoreCase) &&
                ResetDataFile.TryBuildSignText(command.Variant, command.Size, command.Alignment, out string resetText))
            {
                QueueSignText(sign.m_uid, resetText, command.Source);
                return true;
            }

            if (!DynamicSignCaches.TryGetValue(command.Source, out DynamicSignCache cache) ||
                string.IsNullOrWhiteSpace(cache.Text))
            {
                return false;
            }

            QueueSignText(sign.m_uid, cache.Text, command.Source);
            return true;
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

            int maxWrites = Mathf.Max(1, ModConfig.AnnouncementBoardMaxWritesPerUpdate.Value);
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

        private static void MaybeFetchDynamicSign(DynamicSignCommand command, bool force)
        {
            if (string.Equals(command.Kind, "reset", StringComparison.OrdinalIgnoreCase))
            {
                MaybeRefreshResetSign(command, force, resetDataChanged: false);
                return;
            }

            string url = ModConfig.AnnouncementBoardSignTextApiUrl.Value?.Trim() ?? "";
            if (!IsEnabled() || string.IsNullOrWhiteSpace(url) || DynamicSignFetchesInFlight.Contains(command.Source))
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            float interval = GetDynamicSignRefreshInterval(command);
            if (!force &&
                DynamicSignNextFetchTimes.TryGetValue(command.Source, out float nextFetchTime) &&
                now < nextFetchTime)
            {
                Metrics.CacheHits++;
                return;
            }

            DynamicSignNextFetchTimes[command.Source] = now + interval;
            DynamicSignFetchesInFlight.Add(command.Source);
            Metrics.Fetches++;
            ServerSideTweaksPlugin.Instance?.StartCoroutine(FetchDynamicSignText(url, command));
        }

        private static void MaybeRefreshResetSign(DynamicSignCommand command, bool force, bool resetDataChanged)
        {
            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Max(10.0f, ModConfig.AnnouncementBoardResetSignRefreshSeconds.Value);
            if (!force &&
                !resetDataChanged &&
                DynamicSignNextFetchTimes.TryGetValue(command.Source, out float nextRefreshTime) &&
                now < nextRefreshTime)
            {
                Metrics.CacheHits++;
                return;
            }

            DynamicSignNextFetchTimes[command.Source] = now + interval;
            int queued = UpdateDynamicSignBoards(command.Source);
            DebugLog("Queued " + queued + " reset sign board refresh(es) for " + command.DisplayName + ".");
        }

        private static float GetDynamicSignRefreshInterval(DynamicSignCommand command)
        {
            if (string.Equals(command.Kind, "leaderboard", StringComparison.OrdinalIgnoreCase))
            {
                return Mathf.Max(60.0f, ModConfig.AnnouncementBoardLeaderboardRefreshIntervalSeconds.Value);
            }

            if (string.Equals(command.Kind, "player", StringComparison.OrdinalIgnoreCase))
            {
                return Mathf.Max(60.0f, ModConfig.AnnouncementBoardPlayerSignRefreshIntervalSeconds.Value);
            }

            return Mathf.Max(60.0f, ModConfig.AnnouncementBoardLeaderboardRefreshIntervalSeconds.Value);
        }

        private static void RefreshRegisteredBoards(bool resetDataChanged)
        {
            EnsureRegistryLoaded();
            HashSet<string> refreshedSources = new(StringComparer.OrdinalIgnoreCase);
            List<ZDOID> unsupported = new();
            foreach (BoardRegistration board in Boards.Values.ToList())
            {
                if (TryGetDynamicSignCommand(board, out DynamicSignCommand command))
                {
                    if (!refreshedSources.Add(command.Source))
                    {
                        Metrics.CacheHits++;
                        continue;
                    }

                    if (string.Equals(command.Kind, "reset", StringComparison.OrdinalIgnoreCase))
                    {
                        MaybeRefreshResetSign(command, force: false, resetDataChanged);
                    }
                    else
                    {
                        MaybeFetchDynamicSign(command, force: false);
                    }

                    continue;
                }

                if (DynamicSignCommand.TryParseSource(board.Source, out _))
                {
                    unsupported.Add(board.ZdoId);
                }
            }

            foreach (ZDOID id in unsupported)
            {
                Boards.Remove(id);
            }

            if (unsupported.Count > 0)
            {
                SaveRegistry();
            }
        }

        private static IEnumerator FetchDynamicSignText(string baseUrl, DynamicSignCommand command)
        {
            try
            {
                using var request = UnityWebRequest.Get(BuildDynamicSignApiUrl(baseUrl, command));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("User-Agent", "serverSideTweaks/1.1");

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success ||
                    request.responseCode < 200 ||
                    request.responseCode >= 300)
                {
                    string responseBody = request.downloadHandler?.text ?? "";
                    LogLeaderboardWarning(
                        "Dynamic sign fetch failed for " + command.Source + ": " + request.error +
                        " HTTP " + request.responseCode + " " + responseBody);
                    yield break;
                }

                DynamicSignApiResponse? apiResponse = null;
                try
                {
                    apiResponse = JsonUtility.FromJson<DynamicSignApiResponse>(request.downloadHandler?.text ?? "");
                }
                catch (Exception ex)
                {
                    LogLeaderboardWarning("Dynamic sign fetch returned invalid JSON for " + command.Source + ": " + ex.Message);
                    yield break;
                }

                string responseText = apiResponse?.text ?? "";
                if (apiResponse == null || !apiResponse.ok || string.IsNullOrWhiteSpace(responseText))
                {
                    LogLeaderboardWarning("Dynamic sign fetch returned no usable text for " + command.Source + ".");
                    yield break;
                }

                DynamicSignCache nextCache = new(command.Source, apiResponse.generatedAt ?? "", responseText);
                if (DynamicSignCaches.TryGetValue(command.Source, out DynamicSignCache currentCache) &&
                    string.Equals(currentCache.Text, nextCache.Text, StringComparison.Ordinal))
                {
                    DynamicSignCaches[command.Source] = nextCache;
                    Metrics.CacheHits++;
                    DebugLog("Dynamic sign text is unchanged for " + command.DisplayName + ".");
                    yield break;
                }

                DynamicSignCaches[command.Source] = nextCache;
                int updated = UpdateDynamicSignBoards(command.Source);
                ServerSideTweaksPlugin.ModLogger.LogInfo("Updated " + updated + " dynamic sign board(s) for " + command.DisplayName + ".");
            }
            finally
            {
                DynamicSignFetchesInFlight.Remove(command.Source);
            }
        }

        private static string BuildDynamicSignApiUrl(string baseUrl, DynamicSignCommand command)
        {
            Dictionary<string, string> parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = command.Kind,
                ["size"] = command.Size,
                ["alignment"] = command.Alignment,
            };

            if (string.Equals(command.Kind, "leaderboard", StringComparison.OrdinalIgnoreCase))
            {
                parameters["board"] = command.Variant;
            }
            else if (string.Equals(command.Kind, "player", StringComparison.OrdinalIgnoreCase))
            {
                parameters["stat"] = command.Variant;
                parameters["player"] = command.Subject;
            }

            string url = baseUrl;
            foreach (KeyValuePair<string, string> parameter in parameters)
            {
                string separator = url.IndexOf("?", StringComparison.Ordinal) >= 0 ? "&" : "?";
                url += separator + Uri.EscapeDataString(parameter.Key) + "=" + Uri.EscapeDataString(parameter.Value);
            }

            return url;
        }

        private static string ClampText(string rendered)
        {
            int maxCharacters = Mathf.Max(200, ModConfig.AnnouncementBoardMaxCharacters.Value);
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
            if (verb != "!leaderboard" && verb != "!player" && verb != "!reset")
            {
                return false;
            }

            if (!TryParseCommandParameters(tokens.Skip(1), out Dictionary<string, string> parameters) ||
                !TryReadSize(parameters, out string scale) ||
                !TryReadAlignment(parameters, out string alignment))
            {
                return false;
            }

            if (verb == "!leaderboard")
            {
                if (!TryTakeParameter(parameters, "board", out string boardType) ||
                    !string.Equals(boardType.Trim(), LeaderboardDeaths, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (parameters.Count > 0)
                {
                    return false;
                }

                command = DynamicSignCommand.Create("leaderboard", LeaderboardDeaths, "", scale, alignment);
                return true;
            }

            if (verb == "!player")
            {
                if (!TryTakeParameter(parameters, "player", out string playerName))
                {
                    return false;
                }

                string stat = PlayerSummary;
                if (TryTakeParameter(parameters, "stat", out string rawStat) &&
                    !TryNormalizePlayerStat(rawStat, out stat))
                {
                    return false;
                }

                if (parameters.Count > 0 || string.IsNullOrWhiteSpace(playerName))
                {
                    return false;
                }

                command = DynamicSignCommand.Create("player", stat, playerName.Trim(), scale, alignment);
                return true;
            }

            string resetName = "summary";
            if (TryTakeParameter(parameters, "reset", out string rawReset))
            {
                resetName = rawReset.Trim();
            }

            if (parameters.Count > 0 || string.IsNullOrWhiteSpace(resetName))
            {
                return false;
            }

            command = DynamicSignCommand.Create("reset", resetName, "", scale, alignment);
            return true;
        }

        private static bool TryGetDynamicSignCommand(BoardRegistration board, out DynamicSignCommand command)
        {
            return DynamicSignCommand.TryParseSource(board.Source, out command) &&
                IsSupportedDynamicSignCommand(command);
        }

        private static bool IsSupportedDynamicSignCommand(DynamicSignCommand command)
        {
            return IsSupportedLeaderboardCommand(command) ||
                string.Equals(command.Kind, "player", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.Kind, "reset", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedLeaderboardCommand(DynamicSignCommand command)
        {
            return string.Equals(command.Kind, "leaderboard", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(command.Variant, LeaderboardDeaths, StringComparison.OrdinalIgnoreCase);
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

        private static bool TryNormalizePlayerStat(string value, out string stat)
        {
            stat = PlayerSummary;
            string normalized = (value ?? "")
                .Trim()
                .ToLowerInvariant()
                .Replace("_", "-");
            switch (normalized)
            {
                case PlayerSummary:
                case "deaths":
                case "last-online":
                    stat = normalized;
                    return true;
                default:
                    return false;
            }
        }

        private static void LogLeaderboardWarning(string message)
        {
            float now = Time.realtimeSinceStartup;
            float warningInterval = Mathf.Max(
                300.0f,
                ModConfig.AnnouncementBoardLeaderboardRefreshIntervalSeconds.Value);
            if (now < _nextLeaderboardWarningTime)
            {
                DebugLog(message);
                return;
            }

            _nextLeaderboardWarningTime = now + warningInterval;
            ServerSideTweaksPlugin.ModLogger.LogWarning(message);
        }

        private static bool IsEnabled()
        {
            return ModConfig.EnableAnnouncementBoards.Value && IsServerReady();
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
            Boards.Clear();
            string path = ResolveRegistryPath();
            if (!File.Exists(path))
            {
                return;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = line.Split('\t');
                if (parts.Length < 8 ||
                    !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long userId) ||
                    !uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id) ||
                    !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                    !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
                    !DateTimeOffset.TryParse(parts[6], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset createdAt))
                {
                    continue;
                }

                ZDOID zdoId = new(userId, id);
                Boards[zdoId] = new BoardRegistration(zdoId, parts[2], new Vector3(x, y, z), createdAt);
            }

            DebugLog($"Loaded {Boards.Count} announcement board registration(s).");
        }

        private static void SaveRegistry()
        {
            string path = ResolveRegistryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Paths.ConfigPath);
            List<string> lines = new()
            {
                "# userId\tid\tsource\tx\ty\tz\tcreatedAtUtc\tzdo"
            };
            lines.AddRange(Boards.Values
                .OrderBy(board => board.ZdoId.UserID)
                .ThenBy(board => board.ZdoId.ID)
                .Select(board => string.Join("\t",
                    board.ZdoId.UserID.ToString(CultureInfo.InvariantCulture),
                    board.ZdoId.ID.ToString(CultureInfo.InvariantCulture),
                    board.Source.Replace('\t', ' '),
                    board.Position.x.ToString("R", CultureInfo.InvariantCulture),
                    board.Position.y.ToString("R", CultureInfo.InvariantCulture),
                    board.Position.z.ToString("R", CultureInfo.InvariantCulture),
                    board.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    board.ZdoId.ToString())));
            File.WriteAllLines(path, lines);
        }

        private static string ResolveRegistryPath()
        {
            string configured = ModConfig.AnnouncementBoardRegistryFile.Value?.Trim() ?? "";
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Paths.ConfigPath, configured.Length > 0 ? configured : "warpalicious.serverSideTweaks.announcementBoards.tsv");
        }

        private static string FormatVector(Vector3 value)
        {
            return value.x.ToString("0.#", CultureInfo.InvariantCulture) + "," +
                value.y.ToString("0.#", CultureInfo.InvariantCulture) + "," +
                value.z.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugAnnouncementBoards.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private static void MaybeLogMetrics()
        {
            if (!ModConfig.AnnouncementBoardLogMetrics.Value)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Max(30.0f, ModConfig.AnnouncementBoardMetricsLogIntervalSeconds.Value);
            if (now < _nextMetricsLogTime)
            {
                return;
            }

            _nextMetricsLogTime = now + interval;
            ServerSideTweaksPlugin.ModLogger.LogInfo(
                "Announcement boards metrics: " +
                "registered=" + Boards.Count +
                " queued=" + PendingSignWrites.Count +
                " scans=" + Metrics.Scans +
                " scannedSigns=" + Metrics.ScannedSigns +
                " dynamicClaimed=" + Metrics.DynamicClaimed +
                " scanMs=" + Metrics.ScanMs.ToString("0.##", CultureInfo.InvariantCulture) +
                " resetChecks=" + Metrics.ResetFileChecks +
                " resetChanged=" + Metrics.ResetFileChanged +
                " resetReadMs=" + Metrics.ResetFileReadMs.ToString("0.##", CultureInfo.InvariantCulture) +
                " fetches=" + Metrics.Fetches +
                " cacheHits=" + Metrics.CacheHits +
                " writesQueued=" + Metrics.WritesQueued +
                " writesDone=" + Metrics.WritesDone +
                " writesSkipped=" + Metrics.WritesSkipped +
                " writesMissing=" + Metrics.WritesMissing +
                " writeMs=" + Metrics.WriteProcessMs.ToString("0.##", CultureInfo.InvariantCulture));
            Metrics.Clear();
        }

        private sealed class BoardRegistration
        {
            internal BoardRegistration(ZDOID zdoId, string source, Vector3 position, DateTimeOffset createdAt)
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

        private sealed class DynamicSignCache
        {
            internal DynamicSignCache(string source, string generatedAt, string text)
            {
                Source = source;
                GeneratedAt = generatedAt;
                Text = text;
            }

            internal string Source { get; }
            internal string GeneratedAt { get; }
            internal string Text { get; }
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

        private sealed class BoardMetrics
        {
            internal long Scans;
            internal long ScannedSigns;
            internal long DynamicClaimed;
            internal long RegisteredUpdated;
            internal float ScanMs;
            internal long ResetFileChecks;
            internal long ResetFileChanged;
            internal float ResetFileReadMs;
            internal long Fetches;
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
                Fetches = 0;
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
                if (string.Equals(kind, "leaderboard", StringComparison.OrdinalIgnoreCase))
                {
                    return "leaderboard " + variant;
                }

                if (string.Equals(kind, "player", StringComparison.OrdinalIgnoreCase))
                {
                    return "player " + variant + " " + subject;
                }

                return kind;
            }
        }

        [Serializable]
        private sealed class DynamicSignApiResponse
        {
            public bool ok = false;
            public string type = "";
            public string serverName = "";
            public string generatedAt = "";
            public string text = "";
        }
    }
}
