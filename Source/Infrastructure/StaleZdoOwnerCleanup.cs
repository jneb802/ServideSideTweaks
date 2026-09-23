using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ServerSideTweaks.Infrastructure
{
    internal static class StaleZdoOwnerCleanup
    {
        private static readonly FieldInfo? OwnerField = typeof(ZDOExtraData).GetField(
            "s_owner", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly List<ZDOID> StaleOwners = new();
        private static ZDOMan? _world;
        private static float _nextScanTime;
        private static bool _warned;

        internal static void ClearRuntimeCache()
        {
            StaleOwners.Clear();
            _world = null;
            _nextScanTime = 0.0f;
        }

        internal static void Update()
        {
            if (!ReferenceEquals(_world, ZDOMan.instance))
            {
                ClearRuntimeCache();
                _world = ZDOMan.instance;
            }

            if (!ModConfig.EnableStaleZdoOwnerCleanup.Value || ZNet.instance == null ||
                !ZNet.instance.IsServer() || ZDOMan.instance == null)
            {
                return;
            }

            float now = Time.time;
            if (now < _nextScanTime)
            {
                return;
            }

            _nextScanTime = now + Mathf.Max(1.0f, ModConfig.StaleZdoOwnerCleanupIntervalSeconds.Value);
            if (OwnerField?.GetValue(null) is not Dictionary<ZDOID, ushort> owners)
            {
                if (!_warned)
                {
                    ServerSideTweaksPlugin.ModLogger.LogWarning(
                        "Stale ownership cleanup unavailable: ZDOExtraData.s_owner has an unsupported layout.");
                    _warned = true;
                }
                return;
            }

            StaleOwners.Clear();
            foreach (ZDOID zdoId in owners.Keys)
            {
                if (ZDOMan.instance.GetZDO(zdoId) == null)
                {
                    StaleOwners.Add(zdoId);
                }
            }

            foreach (ZDOID zdoId in StaleOwners)
            {
                ZDOExtraData.ReleaseOwner(zdoId);
            }
            StaleOwners.Clear();
        }
    }
}
