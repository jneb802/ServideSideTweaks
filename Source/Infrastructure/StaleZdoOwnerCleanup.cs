using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ServerSideTweaks.Infrastructure
{
    internal static class StaleZdoOwnerCleanup
    {
        private static readonly FieldInfo? OwnerField = typeof(ZDOExtraData).GetField("s_owner", BindingFlags.Static | BindingFlags.NonPublic);
        private static float _nextScanTime;

        internal static void ClearRuntimeCache()
        {
            _nextScanTime = 0.0f;
        }

        internal static void Update()
        {
            if (!ModConfig.EnableStaleZdoOwnerCleanup.Value ||
                ZNet.instance == null ||
                !ZNet.instance.IsServer() ||
                ZDOMan.instance == null)
            {
                return;
            }

            float now = Time.time;
            if (now < _nextScanTime)
            {
                return;
            }

            _nextScanTime = now + Mathf.Max(1.0f, ModConfig.StaleZdoOwnerCleanupIntervalSeconds.Value);
            Cleanup();
        }

        private static void Cleanup()
        {
            Dictionary<ZDOID, ushort>? owners = OwnerField?.GetValue(null) as Dictionary<ZDOID, ushort>;
            if (owners == null || owners.Count == 0)
            {
                return;
            }

            List<ZDOID> stale = new();
            foreach (ZDOID zdoId in owners.Keys)
            {
                if (ZDOMan.instance.GetZDO(zdoId) == null)
                {
                    stale.Add(zdoId);
                }
            }

            foreach (ZDOID zdoId in stale)
            {
                ZDOExtraData.ReleaseOwner(zdoId);
            }
        }
    }
}
