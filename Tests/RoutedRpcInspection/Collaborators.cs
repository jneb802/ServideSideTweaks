// Feature handlers are replaced only in this isolated routing test. The patch,
// dispatcher, ZPackage, ZDOID and ZRoutedRpc are the production implementations.
namespace ServerSideTweaks
{
    internal sealed class Setting
    {
        internal bool Value;
    }

    internal static class ModConfig
    {
        internal static readonly Setting DebugBossLocationDiscovery = new();
        internal static readonly Setting EnableBossMessageRelayBlock = new();
        internal static readonly Setting EnableBossStoneTrophyPlacementBlock = new();
    }

    internal static class ServerSideTweaksPlugin
    {
        internal static readonly TestLogger ModLogger = new();
    }

    internal sealed class TestLogger
    {
        internal int Warnings;
        internal void LogWarning(object message) { Warnings++; }
    }

    internal static class Observed
    {
        internal static int Inspections;
        internal static int LastPayloadSize;
        internal static bool ConsumeDiscovery;
    }
}

namespace ServerSideTweaks.Features.BossStones
{
    internal static class BossLocationDiscoveryDiagnostics
    {
        internal static void LogIncomingRoutedRpc(ZRoutedRpc.RoutedRPCData data)
        {
            ServerSideTweaks.Observed.Inspections++;
            ServerSideTweaks.Observed.LastPayloadSize = data.m_parameters.Size();
        }

        internal static bool TryHandleServerDiscoveryRequest(ZRoutedRpc router, ZRoutedRpc.RoutedRPCData data)
            => ServerSideTweaks.Observed.ConsumeDiscovery;
    }

    internal static class BossStoneTrophyPlacementBlock
    {
        internal static void NotifyBlockedInteraction(ZRoutedRpc.RoutedRPCData data) { }
        internal static bool TryConsumeVisualItem(ZRoutedRpc.RoutedRPCData data) => false;
    }
}

namespace ServerSideTweaks.Features.Bosses
{
    internal static class BossMessage
    {
        internal static bool TryConsumeIncomingRoutedRpc(ZRoutedRpc.RoutedRPCData data) => false;
    }
}
