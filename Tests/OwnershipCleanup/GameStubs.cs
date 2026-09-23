// Minimal game boundary for deterministic cleanup tests. These do not simulate networking.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    internal static class Time { internal static float time; }
    internal static class Mathf { internal static float Max(float a, float b) => Math.Max(a, b); }
}

internal readonly record struct ZDOID(int Value);
internal sealed class ZDO
{
    internal readonly ZDOID m_uid;
    private long _owner;
    internal ZDO(int id) { m_uid = new ZDOID(id); }
    internal long GetOwner() => _owner;
    internal void SetOwner(long owner)
    {
        _owner = owner;
        if (owner == 0) ZDOExtraData.ReleaseOwner(m_uid);
        else ZDOExtraData.AddOwner(m_uid);
    }
}

internal static class ZDOExtraData
{
    private static readonly Dictionary<ZDOID, ushort> s_owner = new();
    internal static void AddOwner(ZDOID id) => s_owner[id] = 1;
    internal static void ReleaseOwner(ZDOID id) => s_owner.Remove(id);
    internal static bool HasOwner(ZDOID id) => s_owner.ContainsKey(id);
    internal static void Clear() => s_owner.Clear();
}

internal sealed class ZDOMan
{
    internal static ZDOMan instance = new();
    internal readonly Dictionary<ZDOID, ZDO> Objects = new();
    internal readonly List<ZDOID> Sent = new();
    internal ZDO? GetZDO(ZDOID id) => Objects.GetValueOrDefault(id);
    internal void ForceSendZDO(ZDOID id) => Sent.Add(id);
}

internal sealed class ZNet
{
    internal static ZNet instance = new();
    internal readonly HashSet<long> Peers = new() { 10, 20 };
    internal bool Server = true;
    internal bool IsServer() => Server;
    internal object? GetPeer(long owner) => Peers.Contains(owner) ? this : null;
}

namespace ServerSideTweaks
{
    internal sealed class Setting<T>
    {
        internal T Value;
        internal Setting(T value) { Value = value; }
    }
    internal static class ModConfig
    {
        internal static readonly Setting<float> OwnershipHandoffReleaseSeconds = new(5);
        internal static readonly Setting<bool> EnableStaleZdoOwnerCleanup = new(true);
        internal static readonly Setting<float> StaleZdoOwnerCleanupIntervalSeconds = new(60);
    }
    internal static class ServerSideTweaksPlugin
    {
        internal static readonly TestLogger ModLogger = new();
    }
    internal sealed class TestLogger
    {
        internal void LogWarning(string message) => throw new Exception(message);
    }
}
