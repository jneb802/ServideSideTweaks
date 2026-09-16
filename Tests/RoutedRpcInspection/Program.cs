using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ServerSideTweaks;
using ServerSideTweaks.Infrastructure.Routing;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Use the local game assemblies; game files are not redistributed.
        string game = Environment.GetEnvironmentVariable("VALHEIM_TEST_INSTALL")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/Valheim");
        string managed = Path.Combine(game, "Valheim.app/Contents/Resources/Data/Managed");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string[] candidates =
            {
                Path.Combine(managed, "publicized_assemblies", name.Name + "_publicized.dll"),
                Path.Combine(managed, name.Name + ".dll"),
                Path.Combine(game, "BepInEx/core", name.Name + ".dll")
            };
            foreach (string candidate in candidates)
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
            return null;
        };
        return Run(args.Length > 0 && args[0] == "--baseline");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(bool baseline)
    {
        MethodInfo method = typeof(ServerSideTweaks.Patches.ZRoutedRpcRpcRoutedRpcPatch)
            .GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
        Func<ZRoutedRpc, ZPackage, bool> prefix =
            (Func<ZRoutedRpc, ZPackage, bool>)method.CreateDelegate(typeof(Func<ZRoutedRpc, ZPackage, bool>));
        ZRoutedRpc router = new(true);
        router.SetUID(100);
        ModConfig.EnableBossMessageRelayBlock.Value = true;
        foreach (int size in new[] { 128, 4096 })
        {
            ZPackage package = Package("UnrelatedTestRpc", ZRoutedRpc.Everybody, ZDOID.None, size);
            for (int i = 0; i < 1000; i++) prefix(router, package);
            int inspections = Observed.Inspections;
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
                if (!prefix(router, package)) throw new Exception("Unrelated request was consumed");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            System.Console.WriteLine($"payload={size} requests=10000 allocatedBytes={allocated} bytesPerRequest={allocated / 10000.0:F1} inspections={Observed.Inspections - inspections}");
            Check(package.GetPos() == 0, "input cursor restored");
            Check(ServerSideTweaksPlugin.ModLogger.Warnings == 0, "no routing warnings");
            if (!baseline)
            {
                Check(Observed.Inspections == inspections, "unrelated requests bypass full decoding");
                Check(allocated == 0, "unrelated inspection allocates zero managed bytes after warmup");
            }
        }
        if (baseline) return 0;
        ModConfig.EnableBossMessageRelayBlock.Value = false;
        CheckRouting(prefix, router);
        System.Console.WriteLine("PASS: allocation and routing checks");
        return 0;
    }

    private static void CheckRouting(Func<ZRoutedRpc, ZPackage, bool> prefix, ZRoutedRpc router)
    {
        ZDOID objectId = new(200, 1);
        CheckInspection(prefix, router, "RPC_DiscoverClosestLocation", 999, ZDOID.None, true);
        CheckInspection(prefix, router, "RPC_DiscoverLocationResponse", 100, ZDOID.None, false);
        ModConfig.DebugBossLocationDiscovery.Value = true;
        CheckInspection(prefix, router, "RPC_DiscoverLocationResponse", 100, ZDOID.None, true);
        CheckInspection(prefix, router, "RPC_RequestOwn", 200, objectId, true);
        CheckInspection(prefix, router, "SetVisualItem", 0, objectId, true);
        CheckInspection(prefix, router, "UnrelatedTestRpc", 100, objectId, false);
        ModConfig.DebugBossLocationDiscovery.Value = false;

        CheckInspection(prefix, router, "ShowMessage", 0, ZDOID.None, false);
        ModConfig.EnableBossMessageRelayBlock.Value = true;
        CheckInspection(prefix, router, "ShowMessage", 0, ZDOID.None, true);
        CheckInspection(prefix, router, "ShowMessage", 200, ZDOID.None, false);
        ModConfig.EnableBossMessageRelayBlock.Value = false;

        CheckInspection(prefix, router, "RPC_RequestOwn", 200, objectId, false);
        CheckInspection(prefix, router, "SetVisualItem", 0, objectId, false);
        ModConfig.EnableBossStoneTrophyPlacementBlock.Value = true;
        CheckInspection(prefix, router, "RPC_RequestOwn", 200, objectId, true);
        CheckInspection(prefix, router, "SetVisualItem", 0, objectId, true);
        CheckInspection(prefix, router, "SetVisualItem", 0, ZDOID.None, false);
        ModConfig.EnableBossStoneTrophyPlacementBlock.Value = false;

        int dispatches = 0;
        RoutedRpcDispatcher.Register("UseDoor", data =>
        {
            Check(data.m_targetZDO == objectId, "target object preserved");
            Check(data.m_parameters.ReadByteArray().Length == 32, "handler receives complete payload");
            dispatches++;
            return RoutedRpcAction.Continue;
        });
        CheckInspection(prefix, router, "UseDoor", 100, objectId, true);
        Check(dispatches == 1, "server-addressed ownership handler runs once");
        CheckInspection(prefix, router, "UseDoor", 200, objectId, false);
        CheckInspection(prefix, router, "UseDoor", 0, objectId, false);
        CheckInspection(prefix, router, "UseDoor", 100, ZDOID.None, false);
        Check(dispatches == 1, "other destinations deferred to normal routing");

        RoutedRpcDispatcher.Register("NewHandler", data => RoutedRpcAction.Consume);
        Check(!prefix(router, Package("NewHandler", 100, objectId, 32)), "newly registered handler can consume request");
        RoutedRpcDispatcher.Clear();
        CheckInspection(prefix, router, "UseDoor", 100, objectId, false);

        RoutedRpcDispatcher.Register("Redirect", data =>
        {
            data.m_targetPeerID = 999;
            return RoutedRpcAction.Continue;
        });
        Check(!prefix(router, Package("Redirect", 100, objectId, 32)), "redirected request not processed twice");
        RoutedRpcDispatcher.Clear();

        Observed.ConsumeDiscovery = true;
        Check(!prefix(router, Package("RPC_DiscoverClosestLocation", 999, ZDOID.None, 32)), "discovery handler can consume request");
        Observed.ConsumeDiscovery = false;

        ZRoutedRpc client = new(false);
        ZPackage clientPackage = Package("RPC_DiscoverClosestLocation", 100, ZDOID.None, 32);
        clientPackage.SetPos(3);
        int before = Observed.Inspections;
        Check(prefix(client, clientPackage) && clientPackage.GetPos() == 3 && Observed.Inspections == before,
            "client path is untouched");

        ZPackage shortHeader = new(new byte[8]);
        int warnings = ServerSideTweaksPlugin.ModLogger.Warnings;
        Check(prefix(router, shortHeader), "truncated header falls back to normal handling");
        Check(shortHeader.GetPos() == 0 && ServerSideTweaksPlugin.ModLogger.Warnings == warnings + 1,
            "truncated header restores cursor and logs warning");

        // Deliberately omit the payload length and bytes. Unrelated requests must
        // not read them; relevant requests retain the existing failure fallback.
        foreach (string name in new[] { "UnrelatedTestRpc", "RPC_DiscoverClosestLocation" })
        {
            ZPackage full = Package(name, 100, ZDOID.None, 32);
            full.ReadLong(); full.ReadLong(); full.ReadLong(); full.ReadZDOID(); full.ReadInt();
            ZPackage headerOnly = new(full.GetArray(), full.GetPos());
            warnings = ServerSideTweaksPlugin.ModLogger.Warnings;
            Check(prefix(router, headerOnly) && headerOnly.GetPos() == 0, "missing payload fallback preserves cursor");
            int expectedWarnings = name == "UnrelatedTestRpc" ? 0 : 1;
            Check(ServerSideTweaksPlugin.ModLogger.Warnings == warnings + expectedWarnings,
                "only relevant requests attempt to read payload");
        }
        System.Console.WriteLine("PASS: discovery, diagnostics, feature toggles, ownership dispatch, redirect, consume, client, malformed input, and payload preservation");
    }

    private static void CheckInspection(Func<ZRoutedRpc, ZPackage, bool> prefix, ZRoutedRpc router,
        string name, long target, ZDOID zdo, bool inspect)
    {
        ZPackage package = Package(name, target, zdo, 32);
        byte[] original = package.GetArray();
        int before = Observed.Inspections;
        int warnings = ServerSideTweaksPlugin.ModLogger.Warnings;
        Check(prefix(router, package), name + " continues to normal handling");
        Check(Observed.Inspections - before == (inspect ? 1 : 0), name + " inspection decision");
        Check(package.GetPos() == 0, name + " restores cursor");
        Check(ServerSideTweaksPlugin.ModLogger.Warnings == warnings, name + " has no warning");
        Check(original.AsSpan().SequenceEqual(package.GetArray()), name + " does not change input bytes");
        if (inspect) Check(Observed.LastPayloadSize == 36, name + " copies complete payload when needed");
    }

    private static ZPackage Package(string name, long target, ZDOID zdo, int payloadSize)
    {
        ZPackage parameters = new();
        parameters.Write(new byte[payloadSize]);
        ZRoutedRpc.RoutedRPCData data = new()
        {
            m_msgID = 1,
            m_senderPeerID = 200,
            m_targetPeerID = target,
            m_targetZDO = zdo,
            m_methodHash = name.GetStableHashCode(),
            m_parameters = parameters
        };
        ZPackage package = new();
        data.Serialize(package);
        package.SetPos(0);
        return package;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
}
