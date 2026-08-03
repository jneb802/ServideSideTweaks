using System.Collections.Generic;
using UnityEngine;

namespace ServerSideTweaks.Infrastructure
{
    internal static class TemporaryOwnershipHandoffs
    {
        private const float CleanupIntervalSeconds = 0.5f;
        private static readonly Dictionary<ZDOID, Entry> Entries = new();
        private static float _nextCleanupTime;

        internal static void ClearRuntimeCache()
        {
            Entries.Clear();
            _nextCleanupTime = 0.0f;
        }

        internal static void Assign(ZDO zdo, long owner, string targetName)
        {
            zdo.SetOwner(owner);
            Track(zdo.m_uid, owner, targetName);
        }

        internal static void RefreshIfTracked(ZDOID zdoId, long owner)
        {
            if (!Entries.TryGetValue(zdoId, out Entry entry) || entry.Owner != owner)
            {
                return;
            }

            Track(zdoId, owner, entry.TargetName);
        }

        internal static void Update()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || Entries.Count == 0)
            {
                return;
            }

            float now = Time.time;
            if (now < _nextCleanupTime)
            {
                return;
            }

            _nextCleanupTime = now + CleanupIntervalSeconds;
            List<ZDOID> release = new();

            foreach (KeyValuePair<ZDOID, Entry> entry in Entries)
            {
                ZDO? zdo = ZDOMan.instance.GetZDO(entry.Key);
                if (zdo == null || now >= entry.Value.ReleaseAfter || ZNet.instance.GetPeer(entry.Value.Owner) == null)
                {
                    release.Add(entry.Key);
                }
            }

            foreach (ZDOID zdoId in release)
            {
                Release(zdoId);
            }
        }

        private static void Track(ZDOID zdoId, long owner, string targetName)
        {
            float releaseAfter = Time.time + Mathf.Max(0.1f, ModConfig.OwnershipHandoffReleaseSeconds.Value);
            Entries[zdoId] = new Entry(owner, releaseAfter, targetName);
        }

        private static void Release(ZDOID zdoId)
        {
            if (!Entries.TryGetValue(zdoId, out Entry entry))
            {
                return;
            }

            Entries.Remove(zdoId);

            ZDO? zdo = ZDOMan.instance.GetZDO(zdoId);
            if (zdo == null)
            {
                ZDOExtraData.ReleaseOwner(zdoId);
                DebugLog($"Released stale {entry.TargetName} owner for missing ZDO {zdoId}.");
                return;
            }

            if (zdo.GetOwner() != entry.Owner)
            {
                return;
            }

            zdo.SetOwner(0L);
            ZDOMan.instance.ForceSendZDO(zdoId);
            DebugLog($"Released temporary {entry.TargetName} owner for {zdoId}.");
        }

        private static void DebugLog(string message)
        {
            if (ModConfig.DebugTemporaryOwnershipHandoffs.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo(message);
            }
        }

        private readonly struct Entry
        {
            internal readonly long Owner;
            internal readonly float ReleaseAfter;
            internal readonly string TargetName;

            internal Entry(long owner, float releaseAfter, string targetName)
            {
                Owner = owner;
                ReleaseAfter = releaseAfter;
                TargetName = targetName;
            }
        }
    }
}
