using System.Collections.Generic;
using UnityEngine;

namespace ServerSideTweaks.Infrastructure
{
    internal static class TemporaryOwnershipHandoffs
    {
        private const float CleanupIntervalSeconds = 0.5f;
        private static readonly Dictionary<ZDOID, Entry> Entries = new();
        private static readonly List<ZDOID> PendingRelease = new();
        private static ZDOMan? _world;
        private static float _nextCleanupTime;

        internal static void ClearRuntimeCache()
        {
            Entries.Clear();
            PendingRelease.Clear();
            _world = null;
            _nextCleanupTime = 0.0f;
        }

        private static void CheckWorld()
        {
            if (!ReferenceEquals(_world, ZDOMan.instance))
            {
                ClearRuntimeCache();
                _world = ZDOMan.instance;
            }
        }

        internal static void Assign(ZDO zdo, long owner)
        {
            CheckWorld();
            zdo.SetOwner(owner);
            Track(zdo.m_uid, owner);
        }

        internal static void RefreshIfTracked(ZDOID zdoId, long owner)
        {
            CheckWorld();
            if (Entries.TryGetValue(zdoId, out Entry entry) && entry.Owner == owner)
            {
                Track(zdoId, owner);
            }
        }

        internal static void Update()
        {
            CheckWorld();
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
            {
                ClearRuntimeCache();
                return;
            }

            float now = Time.time;
            if (Entries.Count == 0 || now < _nextCleanupTime)
            {
                return;
            }

            _nextCleanupTime = now + CleanupIntervalSeconds;
            PendingRelease.Clear();
            foreach (KeyValuePair<ZDOID, Entry> entry in Entries)
            {
                ZDO? zdo = ZDOMan.instance.GetZDO(entry.Key);
                if (zdo == null || zdo.GetOwner() != entry.Value.Owner ||
                    now >= entry.Value.ReleaseAfter || ZNet.instance.GetPeer(entry.Value.Owner) == null)
                {
                    PendingRelease.Add(entry.Key);
                }
            }

            foreach (ZDOID zdoId in PendingRelease)
            {
                Entry entry = Entries[zdoId];
                Entries.Remove(zdoId);
                ZDO? zdo = ZDOMan.instance.GetZDO(zdoId);
                if (zdo == null)
                {
                    ZDOExtraData.ReleaseOwner(zdoId);
                }
                else if (zdo.GetOwner() == entry.Owner)
                {
                    zdo.SetOwner(0L);
                    ZDOMan.instance.ForceSendZDO(zdoId);
                }
            }
            PendingRelease.Clear();
        }

        private static void Track(ZDOID zdoId, long owner)
        {
            float releaseAfter = Time.time + Mathf.Max(0.1f, ModConfig.OwnershipHandoffReleaseSeconds.Value);
            Entries[zdoId] = new Entry(owner, releaseAfter);
        }

        private readonly struct Entry
        {
            internal readonly long Owner;
            internal readonly float ReleaseAfter;

            internal Entry(long owner, float releaseAfter)
            {
                Owner = owner;
                ReleaseAfter = releaseAfter;
            }
        }
    }
}
