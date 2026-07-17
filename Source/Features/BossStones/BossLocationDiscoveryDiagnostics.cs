using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ServerSideTweaks.Features.BossStones
{
    internal static class BossLocationDiscoveryDiagnostics
    {
        private const string DiscoverClosestLocationRpc = "RPC_DiscoverClosestLocation";
        private const string DiscoverLocationResponseRpc = "RPC_DiscoverLocationResponse";
        private const string RequestOwnRpc = "RPC_RequestOwn";
        private const string SetVisualItemRpc = "SetVisualItem";

        private static readonly int DiscoverClosestLocationHash = DiscoverClosestLocationRpc.GetStableHashCode();
        private static readonly int DiscoverLocationResponseHash = DiscoverLocationResponseRpc.GetStableHashCode();
        private static readonly int RequestOwnHash = RequestOwnRpc.GetStableHashCode();
        private static readonly int SetVisualItemHash = SetVisualItemRpc.GetStableHashCode();
        private static readonly System.Reflection.MethodInfo? HandleRoutedRpcMethod =
            AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC");
        private static List<ZoneSystem.LocationInstance> TempLocations = new();

        private readonly struct DiscoveryRequest
        {
            internal readonly string LocationName;
            internal readonly Vector3 Point;
            internal readonly string PinName;
            internal readonly int PinType;
            internal readonly bool ShowMap;
            internal readonly bool DiscoverAll;

            internal DiscoveryRequest(
                string locationName,
                Vector3 point,
                string pinName,
                int pinType,
                bool showMap,
                bool discoverAll)
            {
                LocationName = locationName;
                Point = point;
                PinName = pinName;
                PinType = pinType;
                ShowMap = showMap;
                DiscoverAll = discoverAll;
            }
        }

        internal static void LogIncomingRoutedRpc(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!ShouldLog() || !IsRelevantRoutedRpc(rpcData))
            {
                return;
            }

            LogRoutedRpc("incoming", rpcData);
        }

        internal static void LogRouteRpc(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!ShouldLog() || !IsRelevantRoutedRpc(rpcData))
            {
                return;
            }

            LogRoutedRpc("route", rpcData);
        }

        internal static void LogDiscoverClosestLocationHandler(
            long sender,
            string locationName,
            Vector3 point,
            string pinName,
            int pinType,
            bool showMap,
            bool discoverAll)
        {
            if (!ShouldLog())
            {
                return;
            }

            try
            {
                string senderDescription = DescribePeer(sender);
                string zoneDescription = DescribeZoneSystem(locationName, point, discoverAll);
                ServerSideTweaksPlugin.ModLogger.LogInfo(
                    "[BossLocationDiscovery] handler RPC_DiscoverClosestLocation " +
                    $"sender={senderDescription} locationName=\"{locationName}\" point={FormatVector(point)} " +
                    $"pinName=\"{pinName}\" pinType={pinType} showMap={showMap} discoverAll={discoverAll} {zoneDescription}");
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"[BossLocationDiscovery] handler diagnostic failed: {ex}");
            }
        }

        internal static bool TryHandleServerDiscoveryRequest(ZRoutedRpc routedRpc, ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!IsServerReady() ||
                rpcData.m_methodHash != DiscoverClosestLocationHash ||
                !routedRpc.m_server)
            {
                return false;
            }

            bool targetIsLocal = rpcData.m_targetPeerID == routedRpc.m_id;
            bool targetIsEverybody = rpcData.m_targetPeerID == ZRoutedRpc.Everybody;
            bool targetPeerExists = ZNet.instance != null && ZNet.instance.GetPeer(rpcData.m_targetPeerID) != null;
            bool handlerRegistered = routedRpc.m_functions.ContainsKey(rpcData.m_methodHash);

            if (ShouldLog())
            {
                LogServerTargetDecision(routedRpc, rpcData, targetIsLocal, targetIsEverybody, targetPeerExists, handlerRegistered);
            }

            if (handlerRegistered)
            {
                if (targetIsLocal || targetIsEverybody || targetPeerExists)
                {
                    return false;
                }

                return TryDispatchMissingServerTarget(routedRpc, rpcData);
            }

            if (!targetIsLocal && !targetIsEverybody && targetPeerExists)
            {
                return false;
            }

            return TryHandleMissingVanillaHandler(routedRpc, rpcData);
        }

        private static bool TryDispatchMissingServerTarget(ZRoutedRpc routedRpc, ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (HandleRoutedRpcMethod == null)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning(
                    "[BossLocationDiscovery] cannot repair missing server target because HandleRoutedRPC was not found");
                return false;
            }

            try
            {
                rpcData.m_parameters.SetPos(0);
                HandleRoutedRpcMethod.Invoke(routedRpc, new object[] { rpcData });
                rpcData.m_parameters.SetPos(0);
                if (ShouldLog())
                {
                    ServerSideTweaksPlugin.ModLogger.LogInfo(
                        "[BossLocationDiscovery] repaired missing server target by dispatching RPC_DiscoverClosestLocation locally");
                }
                return true;
            }
            catch (Exception ex)
            {
                rpcData.m_parameters.SetPos(0);
                ServerSideTweaksPlugin.ModLogger.LogWarning(
                    $"[BossLocationDiscovery] failed to repair missing server target: {ex}");
                return false;
            }
        }

        private static bool TryHandleMissingVanillaHandler(ZRoutedRpc routedRpc, ZRoutedRpc.RoutedRPCData rpcData)
        {
            try
            {
                DiscoveryRequest request = ReadDiscoveryRequest(rpcData);
                if (ShouldLog())
                {
                    ServerSideTweaksPlugin.ModLogger.LogInfo(
                        "[BossLocationDiscovery] vanilla handler missing; handling RPC_DiscoverClosestLocation in serverSideTweaks");
                }

                if (request.DiscoverAll)
                {
                    List<ZoneSystem.LocationInstance> locations = new();
                    if (!ZoneSystem.instance.FindLocations(request.LocationName, ref locations))
                    {
                        ServerSideTweaksPlugin.ModLogger.LogWarning(
                            $"[BossLocationDiscovery] fallback failed to find locations of type {request.LocationName}");
                        return true;
                    }

                    if (ShouldLog())
                    {
                        ServerSideTweaksPlugin.ModLogger.LogInfo(
                            $"[BossLocationDiscovery] fallback found {locations.Count} location(s) of type {request.LocationName}");
                    }

                    foreach (ZoneSystem.LocationInstance location in locations)
                    {
                        routedRpc.InvokeRoutedRPC(
                            rpcData.m_senderPeerID,
                            DiscoverLocationResponseRpc,
                            request.PinName,
                            request.PinType,
                            location.m_position,
                            request.ShowMap);
                    }

                    return true;
                }

                if (!ZoneSystem.instance.FindClosestLocation(request.LocationName, request.Point, out ZoneSystem.LocationInstance closest))
                {
                    ServerSideTweaksPlugin.ModLogger.LogWarning(
                        $"[BossLocationDiscovery] fallback failed to find location of type {request.LocationName}");
                    return true;
                }

                if (ShouldLog())
                {
                    float distance = Vector3.Distance(request.Point, closest.m_position);
                    ServerSideTweaksPlugin.ModLogger.LogInfo(
                        $"[BossLocationDiscovery] fallback found location of type {request.LocationName} at {FormatVector(closest.m_position)} distance={distance:0.#}; sending RPC_DiscoverLocationResponse to {DescribePeer(rpcData.m_senderPeerID)}");
                }
                routedRpc.InvokeRoutedRPC(
                    rpcData.m_senderPeerID,
                    DiscoverLocationResponseRpc,
                    request.PinName,
                    request.PinType,
                    closest.m_position,
                    request.ShowMap);
                return true;
            }
            catch (Exception ex)
            {
                rpcData.m_parameters.SetPos(0);
                ServerSideTweaksPlugin.ModLogger.LogWarning(
                    $"[BossLocationDiscovery] fallback handler failed: {ex}");
                return false;
            }
        }

        private static DiscoveryRequest ReadDiscoveryRequest(ZRoutedRpc.RoutedRPCData rpcData)
        {
            ZPackage parameters = new(rpcData.m_parameters.GetArray());
            DiscoveryRequest request = new(
                parameters.ReadString(),
                parameters.ReadVector3(),
                parameters.ReadString(),
                parameters.ReadInt(),
                parameters.ReadBool(),
                parameters.ReadBool());
            rpcData.m_parameters.SetPos(0);
            return request;
        }

        private static void LogRoutedRpc(string stage, ZRoutedRpc.RoutedRPCData rpcData)
        {
            try
            {
                string methodName = GetMethodName(rpcData.m_methodHash);
                string sender = DescribePeer(rpcData.m_senderPeerID);
                string targetPeer = DescribeTargetPeer(rpcData.m_targetPeerID);
                string targetZdo = DescribeTargetZdo(rpcData.m_targetZDO);
                string parameters = DescribeParameters(rpcData);
                ServerSideTweaksPlugin.ModLogger.LogInfo(
                    "[BossLocationDiscovery] " + stage + " routed RPC " +
                    $"method={methodName} msgID={rpcData.m_msgID} sender={sender} targetPeer={targetPeer} " +
                    $"targetZDO={targetZdo} params={parameters}");
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"[BossLocationDiscovery] {stage} routed RPC diagnostic failed: {ex}");
            }
        }

        private static void LogServerTargetDecision(
            ZRoutedRpc routedRpc,
            ZRoutedRpc.RoutedRPCData rpcData,
            bool targetIsLocal,
            bool targetIsEverybody,
            bool targetPeerExists,
            bool handlerRegistered)
        {
            long sessionId = ZDOMan.instance != null ? ZDOMan.GetSessionID() : 0L;
            ServerSideTweaksPlugin.ModLogger.LogInfo(
                "[BossLocationDiscovery] server target check " +
                $"method=RPC_DiscoverClosestLocation targetPeer={rpcData.m_targetPeerID} " +
                $"routedRpcLocalId={routedRpc.m_id} sessionId={sessionId} " +
                $"targetIsLocal={targetIsLocal} targetIsEverybody={targetIsEverybody} " +
                $"targetPeerExists={targetPeerExists} handlerRegistered={handlerRegistered}");
        }

        private static bool IsRelevantRoutedRpc(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (rpcData.m_methodHash == DiscoverClosestLocationHash ||
                rpcData.m_methodHash == DiscoverLocationResponseHash)
            {
                return true;
            }

            if ((rpcData.m_methodHash == RequestOwnHash || rpcData.m_methodHash == SetVisualItemHash) &&
                IsBossStoneTarget(rpcData.m_targetZDO))
            {
                return true;
            }

            return false;
        }

        private static bool IsBossStoneTarget(ZDOID zdoId)
        {
            if (zdoId.IsNone() || ZDOMan.instance == null)
            {
                return false;
            }

            ZDO? zdo = ZDOMan.instance.GetZDO(zdoId);
            return zdo != null && BossStoneTrophyPlacementBlock.IsBossStoneZdo(zdo);
        }

        private static string DescribeParameters(ZRoutedRpc.RoutedRPCData rpcData)
        {
            ZPackage parameters = new(rpcData.m_parameters.GetArray());

            if (rpcData.m_methodHash == DiscoverClosestLocationHash)
            {
                string locationName = parameters.ReadString();
                Vector3 point = parameters.ReadVector3();
                string pinName = parameters.ReadString();
                int pinType = parameters.ReadInt();
                bool showMap = parameters.ReadBool();
                bool discoverAll = parameters.ReadBool();
                return $"locationName=\"{locationName}\" point={FormatVector(point)} pinName=\"{pinName}\" pinType={pinType} showMap={showMap} discoverAll={discoverAll}";
            }

            if (rpcData.m_methodHash == DiscoverLocationResponseHash)
            {
                string pinName = parameters.ReadString();
                int pinType = parameters.ReadInt();
                Vector3 position = parameters.ReadVector3();
                bool showMap = parameters.ReadBool();
                return $"pinName=\"{pinName}\" pinType={pinType} position={FormatVector(position)} showMap={showMap}";
            }

            if (rpcData.m_methodHash == SetVisualItemHash)
            {
                string itemName = parameters.ReadString();
                int variant = parameters.ReadInt();
                int quality = parameters.ReadInt();
                int orientation = parameters.ReadInt();
                return $"itemName=\"{itemName}\" variant={variant} quality={quality} orientation={orientation}";
            }

            return $"bytes={rpcData.m_parameters.Size()}";
        }

        private static string DescribeZoneSystem(string locationName, Vector3 point, bool discoverAll)
        {
            if (ZoneSystem.instance == null)
            {
                return "zoneSystem=missing";
            }

            int placedCount = CountPlacedLocations(locationName);
            if (discoverAll)
            {
                TempLocations.Clear();
                bool foundLocations = ZoneSystem.instance.FindLocations(locationName, ref TempLocations);
                return $"zoneSystem=present placedMatching={placedCount} findLocations={foundLocations} findCount={TempLocations.Count} samples={FormatLocationSamples(TempLocations)}";
            }

            bool foundClosest = ZoneSystem.instance.FindClosestLocation(locationName, point, out ZoneSystem.LocationInstance closest);
            if (!foundClosest)
            {
                return $"zoneSystem=present placedMatching={placedCount} findClosest=False";
            }

            float distance = Vector3.Distance(point, closest.m_position);
            return $"zoneSystem=present placedMatching={placedCount} findClosest=True closest={FormatVector(closest.m_position)} distance={distance:0.#}";
        }

        private static int CountPlacedLocations(string locationName)
        {
            int count = 0;
            foreach (ZoneSystem.LocationInstance location in ZoneSystem.instance.GetLocationList())
            {
                ZoneSystem.ZoneLocation zoneLocation = location.m_location;
                if (zoneLocation == null)
                {
                    continue;
                }

                if (string.Equals(zoneLocation.m_prefabName, locationName, StringComparison.Ordinal) ||
                    (zoneLocation.m_prefab != null && string.Equals(zoneLocation.m_prefab.Name, locationName, StringComparison.Ordinal)))
                {
                    count++;
                }
            }

            return count;
        }

        private static string FormatLocationSamples(List<ZoneSystem.LocationInstance> locations)
        {
            if (locations.Count == 0)
            {
                return "none";
            }

            int limit = Math.Min(locations.Count, 3);
            List<string> samples = new(limit);
            for (int i = 0; i < limit; i++)
            {
                samples.Add(FormatVector(locations[i].m_position));
            }

            return string.Join(",", samples);
        }

        private static string DescribeTargetZdo(ZDOID zdoId)
        {
            if (zdoId.IsNone())
            {
                return "none";
            }

            if (ZDOMan.instance == null)
            {
                return $"{zdoId}(zdoMan=missing)";
            }

            ZDO? zdo = ZDOMan.instance.GetZDO(zdoId);
            if (zdo == null)
            {
                return $"{zdoId}(missing)";
            }

            string prefabName = GetPrefabName(zdo.GetPrefab());
            return $"{zdoId}(prefab={prefabName} owner={zdo.GetOwner()} hasOwner={zdo.HasOwner()} pos={FormatVector(zdo.GetPosition())})";
        }

        private static string DescribePeer(long peerId)
        {
            if (peerId == ZRoutedRpc.Everybody)
            {
                return "everybody";
            }

            if (ZNet.instance == null)
            {
                return $"{peerId}(znet=missing)";
            }

            ZNetPeer peer = ZNet.instance.GetPeer(peerId);
            if (peer == null)
            {
                return $"{peerId}(peer=missing)";
            }

            return $"{peerId}(name=\"{peer.m_playerName}\" ready={peer.IsReady()} pos={FormatVector(peer.m_refPos)})";
        }

        private static string DescribeTargetPeer(long peerId)
        {
            if (peerId == ZRoutedRpc.Everybody)
            {
                return "everybody";
            }

            return DescribePeer(peerId);
        }

        private static string GetMethodName(int methodHash)
        {
            if (methodHash == DiscoverClosestLocationHash)
            {
                return DiscoverClosestLocationRpc;
            }

            if (methodHash == DiscoverLocationResponseHash)
            {
                return DiscoverLocationResponseRpc;
            }

            if (methodHash == RequestOwnHash)
            {
                return RequestOwnRpc;
            }

            if (methodHash == SetVisualItemHash)
            {
                return SetVisualItemRpc;
            }

            return methodHash.ToString();
        }

        private static string GetPrefabName(int prefabHash)
        {
            if (ZNetScene.instance == null)
            {
                return prefabHash.ToString();
            }

            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            if (prefab == null)
            {
                return prefabHash.ToString();
            }

            return prefab.name;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.#},{value.y:0.#},{value.z:0.#}";
        }

        private static bool IsServerReady()
        {
            return ZNet.instance != null &&
                ZNet.instance.IsServer();
        }

        private static bool ShouldLog()
        {
            return ModConfig.DebugBossLocationDiscovery.Value &&
                IsServerReady();
        }
    }
}
