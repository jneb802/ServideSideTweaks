using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SstInspectionProbe;

// Test-only helper. Never include this DLL in a production release.
[BepInPlugin("praetoris.sst-inspection-probe", "SST Inspection Probe", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string PayloadRpc = "SstProbePayload";
    private const string ControlRpc = "SstProbeControl";
    private static Plugin Instance = null!;
    private static Func<long> Allocated = () => -1;
    [ThreadStatic] private static Measurement? ActiveInspection;
    private static readonly Dictionary<int, Measurement> Measurements = new();
    private readonly Harmony _harmony = new("praetoris.sst-inspection-probe");
    private StreamWriter? _writer;
    private ZRoutedRpc? _registered;
    private bool _patched;
    private bool _deliveryPatched;
    private string _phase = "startup";
    private float _nextSample;
    private byte[] _payload = Array.Empty<byte>();
    private int _remaining;
    private int _sequence;
    private int _perFrame;
    private long _received;
    private long _invalid;
    private bool _protectPlayer;
    private Player? _protectedPlayer;
    private readonly Dictionary<long, int> _lastSequence = new();

    [DllImport("libmonobdwgc-2.0.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern UIntPtr GC_get_total_bytes();

    private void Awake()
    {
        Instance = this;
        MethodInfo? allocationMethod = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
        if (allocationMethod != null)
        {
            Func<long> counter = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), allocationMethod);
            try
            {
                long before = counter();
                byte[] test = new byte[4096];
                long after = counter();
                GC.KeepAlive(test);
                if (after > before) Allocated = counter;
            }
            catch (Exception ex) { Logger.LogWarning("Allocation counter unavailable: " + ex.GetType().Name); }
        }
        string counterKind = "thread";
        if (Allocated() < 0)
        {
            counterKind = "unsupported";
            try
            {
                long before = (long)GC_get_total_bytes().ToUInt64();
                byte[] test = new byte[4096];
                long after = (long)GC_get_total_bytes().ToUInt64();
                GC.KeepAlive(test);
                if (after > before)
                {
                    Allocated = () => (long)GC_get_total_bytes().ToUInt64();
                    counterKind = "process-total-boehm";
                }
            }
            catch (Exception ex) { Logger.LogWarning("Native allocation counter unavailable: " + ex.GetType().Name); }
        }
        Logger.LogInfo("Allocation counter kind=" + counterKind);
        foreach (ConstructorInfo constructor in typeof(ZPackage).GetConstructors())
            _harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(Plugin), nameof(PackageCreated)));
        new Terminal.ConsoleCommand("sst_probe_burst", "Test only: count payloadBytes messagesPerFrame", args =>
        {
            if (args.Length != 4 || Player.m_localPlayer == null) return;
            if (_remaining > 0)
            {
                args.Context.AddString("PROBE previous burst is still running");
                return;
            }
            _remaining = Math.Min(20000, Math.Max(1, int.Parse(args[1])));
            int size = Math.Min(8192, Math.Max(1, int.Parse(args[2])));
            _perFrame = Math.Min(20, Math.Max(1, int.Parse(args[3])));
            _sequence = 0;
            _payload = new byte[size];
            for (int i = 0; i < size; i++) _payload[i] = (byte)(i % 251);
            args.Context.AddString($"PROBE burst queued count={_remaining} payload={size} perFrame={_perFrame}");
        });
        new Terminal.ConsoleCommand("sst_probe_mark", "Test only: mark a measurement stage", args =>
        {
            if (args.Length < 2) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ControlRpc, "mark", args[1]);
            args.Context.AddString("PROBE mark=" + args[1]);
        });
        new Terminal.ConsoleCommand("sst_probe_status", "Show test progress", args =>
            args.Context.AddString($"PROBE remaining={_remaining} sent={_sequence} received={_received} invalid={_invalid}"));
        new Terminal.ConsoleCommand("sst_probe_arm_safety", "Test only: protect the development character when it loads", args =>
        {
            _protectPlayer = true;
            args.Context.AddString("PROBE development-character safety armed");
        });
        new Terminal.ConsoleCommand("sst_probe_inspect", "Test only: inspect nearest matching prefab", args =>
        {
            ZNetView? view = args.Length > 1 ? FindClosest(args[1]) : null;
            if (view == null) { args.Context.AddString("PROBE no matching object"); return; }
            ZDO zdo = view.GetZDO();
            args.Context.AddString($"PROBE object={view.name} id={zdo.m_uid} owner={zdo.GetOwner()} state={zdo.GetInt("state")} picked={zdo.GetBool("picked")} position={view.transform.position}");
        });
        new Terminal.ConsoleCommand("sst_probe_interact", "Test only: interact with nearest matching prefab", args =>
        {
            ZNetView? view = args.Length > 1 ? FindClosest(args[1]) : null;
            Interactable? target = view == null ? null : view.GetComponent<Interactable>();
            if (target == null) { args.Context.AddString("PROBE no matching interactable"); return; }
            bool result = target.Interact(Player.m_localPlayer, false, false);
            args.Context.AddString("PROBE interaction=" + result + " object=" + view!.name);
        });
        new Terminal.ConsoleCommand("sst_probe_damage", "Test only: damage nearest matching prefab by amount", args =>
        {
            ZNetView? view = args.Length > 2 ? FindClosest(args[1]) : null;
            IDestructible? target = view == null ? null : view.GetComponent<IDestructible>();
            if (target == null) { args.Context.AddString("PROBE no matching destructible"); return; }
            HitData hit = new();
            hit.m_damage.m_damage = Math.Max(0, float.Parse(args[2], CultureInfo.InvariantCulture));
            hit.m_point = view!.transform.position;
            hit.m_dir = Vector3.down;
            hit.m_toolTier = 10;
            hit.SetAttacker(Player.m_localPlayer);
            target.Damage(hit);
            args.Context.AddString("PROBE damage sent object=" + view.name);
        });
        new Terminal.ConsoleCommand("sst_probe_teleport", "Test only: move development character to x y z", args =>
        {
            if (args.Length != 4 || Player.m_localPlayer == null) return;
            Vector3 point = new(float.Parse(args[1], CultureInfo.InvariantCulture),
                float.Parse(args[2], CultureInfo.InvariantCulture), float.Parse(args[3], CultureInfo.InvariantCulture));
            bool accepted = Player.m_localPlayer.TeleportTo(point, Player.m_localPlayer.transform.rotation, true);
            args.Context.AddString("PROBE teleportAccepted=" + accepted + " target=" + point);
        });
        new Terminal.ConsoleCommand("sst_probe_message", "Send a normal or boss announcement", args =>
        {
            string message = args.Length > 1 && args[1] == "boss" ? "$enemy_boss_bonemass_spawnmessage" : "SST normal test message";
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)MessageHud.MessageType.Center, message);
            args.Context.AddString("PROBE message sent " + message);
        });
        new Terminal.ConsoleCommand("sst_probe_discovery", "Request nearest Eikthyr location through server", args =>
        {
            if (Player.m_localPlayer == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC("RPC_DiscoverClosestLocation",
                "Eikthyrnir", Player.m_localPlayer.transform.position, "$enemy_eikthyr", 0, false, false);
            args.Context.AddString("PROBE discovery request sent");
        });
        new Terminal.ConsoleCommand("sst_probe_owner", "Test only: prefab [UseDoor|RPC_Pick]; set server owner, optionally send a server-addressed interaction", args =>
        {
            if (args.Length < 2 || Player.m_localPlayer == null) return;
            ZNetView? closest = FindClosest(args[1]);
            if (closest == null) { args.Context.AddString("PROBE no matching object"); return; }
            ZDOID id = closest.GetZDO().m_uid;
            ZRoutedRpc.instance.InvokeRoutedRPC("SstProbeOwner", id);
            args.Context.AddString("PROBE ownership preparation sent " + closest.name);
            if (args.Length > 2 && (args[2] == "UseDoor" || args[2] == "RPC_Pick"))
            {
                object parameter = args[2] == "UseDoor" ? (object)true : 0;
                ZRoutedRpc.instance.InvokeRoutedRPC(ZNet.instance.GetServerPeer().m_uid, id, args[2], parameter);
                args.Context.AddString("PROBE server-addressed " + args[2] + " sent for " + id);
            }
        });
    }

    private void Update()
    {
        Player player = Player.m_localPlayer;
        if (_protectPlayer && player != null && player != _protectedPlayer)
        {
            player.SetGodMode(true);
            player.SetGhostMode(true);
            _protectedPlayer = player;
            Logger.LogInfo("PROBE development-character safety applied");
        }
        ZRoutedRpc rpc = ZRoutedRpc.instance;
        if (rpc != null && !ReferenceEquals(rpc, _registered))
        {
            _registered = rpc;
            rpc.Register<ZPackage>(PayloadRpc, ReceivePayload);
            rpc.Register<string, string>(ControlRpc, Control);
            rpc.Register<ZDOID>("SstProbeOwner", (sender, id) =>
            {
                if (!IsServer()) return;
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null) return;
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.ForceSendZDO(id);
                Logger.LogInfo("PROBE set server owner for " + id);
            });
            if (!_deliveryPatched)
            {
                _harmony.Patch(AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(ObserveDelivery)));
                _deliveryPatched = true;
            }
        }
        if (!_patched && IsServer())
        {
            Type? type = AccessTools.TypeByName("ServerSideTweaks.Patches.ZRoutedRpcRpcRoutedRpcPatch");
            if (type != null)
            {
                _harmony.Patch(AccessTools.Method(type, "Prefix"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeInspection)),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterInspection)));
                _patched = true;
                Logger.LogInfo("PROBE attached to ServerSideTweaks incoming prefix");
                _writer = new StreamWriter(Path.Combine(Paths.BepInExRootPath, "sst-inspection-probe.csv"), true);
                _writer.AutoFlush = true;
                _writer.WriteLine("utc,phase,method,calls,inspectionAllocated,inspectionTicks,packages,packagePayloadBytes,received,invalid,runtimeAllocated,managedBytes,gc0,gc1,gc2,rssBytes");
                HarmonyLib.Patches patches = Harmony.GetPatchInfo(AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC"));
                foreach (Patch patch in patches.Prefixes)
                    Logger.LogInfo("PROBE incoming patch=" + patch.owner + " priority=" + patch.priority);
            }
        }
        if (_remaining > 0 && !IsServer() && Player.m_localPlayer != null && rpc != null)
        {
            for (int i = 0; i < _perFrame && _remaining > 0; i++)
            {
                ZPackage package = new();
                package.Write(_sequence++);
                package.Write(_payload);
                rpc.InvokeRoutedRPC(PayloadRpc, package);
                _remaining--;
            }
            if (_remaining == 0) Logger.LogInfo("PROBE burst sent=" + _sequence);
        }
        if (_writer != null && Time.realtimeSinceStartup >= _nextSample)
        {
            _nextSample = Time.realtimeSinceStartup + 5;
            WriteSnapshot();
        }
    }

    private static bool IsServer() => ZNet.instance != null && ZNet.instance.IsServer();

    private static ZNetView? FindClosest(string prefix)
    {
        if (Player.m_localPlayer == null) return null;
        ZNetView? closest = null;
        float distance = 30;
        foreach (ZNetView view in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
        {
            if (view == null || !view.IsValid() || !view.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            float candidate = Vector3.Distance(view.transform.position, Player.m_localPlayer.transform.position);
            if (candidate < distance) { closest = view; distance = candidate; }
        }
        return closest;
    }

    private void ReceivePayload(long sender, ZPackage package)
    {
        if (!IsServer()) return;
        int sequence = package.ReadInt();
        byte[] bytes = package.ReadByteArray();
        if (sequence != 0 && (!_lastSequence.TryGetValue(sender, out int last) || sequence != last + 1)) _invalid++;
        _lastSequence[sender] = sequence;
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] != (byte)(i % 251)) { _invalid++; break; }
        _received++;
    }

    private void Control(long sender, string command, string value)
    {
        if (!IsServer() || command != "mark") return;
        WriteSnapshot();
        _phase = value.Replace(',', '_').Replace('\n', '_').Replace('\r', '_');
        WriteSnapshot();
        Logger.LogInfo("PROBE phase=" + _phase + " received=" + _received + " invalid=" + _invalid);
    }

    private static void ObserveDelivery(ZRoutedRpc.RoutedRPCData data)
    {
        if (data.m_methodHash == "ShowMessage".GetStableHashCode() || data.m_methodHash == "RPC_DiscoverLocationResponse".GetStableHashCode())
            Instance.Logger.LogInfo("PROBE delivered method=" + data.m_methodHash + " server=" + IsServer());
    }

    private static void BeforeInspection(ZPackage pkg, out InspectionState __state)
    {
        int position = pkg.GetPos();
        int hash = 0;
        try
        {
            pkg.SetPos(0);
            pkg.ReadLong(); pkg.ReadLong(); pkg.ReadLong(); pkg.ReadZDOID();
            hash = pkg.ReadInt();
        }
        finally { pkg.SetPos(position); }
        if (!Measurements.TryGetValue(hash, out Measurement? measurement))
        {
            measurement = new Measurement();
            Measurements.Add(hash, measurement);
        }
        __state = new InspectionState { Measurement = measurement, Parent = ActiveInspection, Started = Stopwatch.GetTimestamp(), Allocated = Allocated() };
        ActiveInspection = measurement;
    }

    private static void AfterInspection(InspectionState __state)
    {
        long allocated = Allocated();
        ActiveInspection = __state.Parent;
        __state.Measurement.Calls++;
        if (allocated >= 0) __state.Measurement.Allocated += allocated - __state.Allocated;
        else __state.Measurement.Allocated = -1;
        __state.Measurement.Ticks += Stopwatch.GetTimestamp() - __state.Started;
    }

    private static void PackageCreated(ZPackage __instance)
    {
        if (ActiveInspection == null) return;
        ActiveInspection.Packages++;
        ActiveInspection.Capacity += __instance.Size();
    }

    private void WriteSnapshot()
    {
        if (_writer == null) return;
        long allocation = Allocated();
        long heap = GC.GetTotalMemory(false);
        long rss;
        using (Process process = Process.GetCurrentProcess()) rss = process.WorkingSet64;
        string suffix = $",{_received},{_invalid},{allocation},{heap},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{rss}";
        foreach (KeyValuePair<int, Measurement> pair in Measurements)
        {
            Measurement m = pair.Value;
            _writer.WriteLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + $",{_phase},{pair.Key},{m.Calls},{m.Allocated},{m.Ticks},{m.Packages},{m.Capacity}" + suffix);
        }
    }

    private void OnDestroy()
    {
        WriteSnapshot();
        _writer?.Dispose();
        _harmony.UnpatchSelf();
    }

    private sealed class Measurement { internal long Calls; internal long Allocated; internal long Ticks; internal long Packages; internal long Capacity; }
    private struct InspectionState { internal Measurement Measurement; internal Measurement? Parent; internal long Allocated; internal long Started; }
}
