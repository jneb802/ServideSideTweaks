using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ServerSideTweaks.Infrastructure
{
    internal static class HarmonyPatchDiagnostics
    {
        private static bool _logged;

        internal static void LogOnceWhenReady()
        {
            if (_logged ||
                !ModConfig.DebugBossLocationDiscovery.Value ||
                ZNet.instance == null ||
                ZRoutedRpc.instance == null)
            {
                return;
            }

            _logged = true;

            try
            {
                LogPatchInfo("ZRoutedRpc.RPC_RoutedRPC", AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC"));
                LogPatchInfo("ZRoutedRpc.RouteRPC", AccessTools.Method(typeof(ZRoutedRpc), "RouteRPC"));
                LogPatchInfo("ZRoutedRpc.HandleRoutedRPC", AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC"));
                LogPatchInfo("Game.RPC_DiscoverClosestLocation", AccessTools.Method(typeof(Game), "RPC_DiscoverClosestLocation"));
                LogPatchInfo("Game.RPC_DiscoverLocationResponse", AccessTools.Method(typeof(Game), "RPC_DiscoverLocationResponse"));
            }
            catch (Exception ex)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"[HarmonyPatchDiagnostics] Failed to log Harmony patch diagnostics: {ex}");
            }
        }

        private static void LogPatchInfo(string label, MethodBase method)
        {
            if (method == null)
            {
                ServerSideTweaksPlugin.ModLogger.LogWarning($"[HarmonyPatchDiagnostics] {label}: method not found.");
                return;
            }

            HarmonyLib.Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo($"[HarmonyPatchDiagnostics] {label}: no Harmony patches.");
                return;
            }

            LogPatchList(label, "prefix", patches.Prefixes);
            LogPatchList(label, "postfix", patches.Postfixes);
            LogPatchList(label, "transpiler", patches.Transpilers);
            LogPatchList(label, "finalizer", patches.Finalizers);
        }

        private static void LogPatchList(string label, string kind, IReadOnlyList<Patch> patches)
        {
            if (patches == null || patches.Count == 0)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo($"[HarmonyPatchDiagnostics] {label} {kind}: none");
                return;
            }

            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                MethodInfo patchMethod = patch.PatchMethod;
                string methodName = patchMethod == null
                    ? "unknown"
                    : $"{patchMethod.DeclaringType?.FullName}.{patchMethod.Name}";

                ServerSideTweaksPlugin.ModLogger.LogInfo(
                    $"[HarmonyPatchDiagnostics] {label} {kind}[{i}] " +
                    $"owner={patch.owner} priority={patch.priority} index={patch.index} " +
                    $"before={FormatOwners(patch.before)} after={FormatOwners(patch.after)} method={methodName}");
            }
        }

        private static string FormatOwners(string[] owners)
        {
            if (owners == null || owners.Length == 0)
            {
                return "[]";
            }

            StringBuilder builder = new("[");
            for (int i = 0; i < owners.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(owners[i]);
            }

            builder.Append(']');
            return builder.ToString();
        }
    }
}
