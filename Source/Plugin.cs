using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ServerSideTweaks.Features.Bosses;
using ServerSideTweaks.Features.Doors;
using ServerSideTweaks.Features.Fermenters;
using ServerSideTweaks.Features.Harvest;
using ServerSideTweaks.Features.Locations;
using ServerSideTweaks.Features.Mining;
using ServerSideTweaks.Features.Pickables;
using ServerSideTweaks.Features.Trees;
using ServerSideTweaks.Features.ValheimEnforcer;
using ServerSideTweaks.Features.Vendors;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class ServerSideTweaksPlugin : BaseUnityPlugin
    {
        private const string ModName = "serverSideTweaks";
        private const string ModVersion = "1.1.9";
        private const string ModGUID = "warpalicious.serverSideTweaks";
        private const double UpdateFailureLogIntervalSeconds = 30.0;

        private readonly Harmony _harmony = new(ModGUID);
        private readonly Dictionary<string, DateTime> _lastUpdateFailureLogTimes = new(StringComparer.Ordinal);
        private ConfigWatcher? _configWatcher;

        internal static ServerSideTweaksPlugin? Instance { get; private set; }
        internal static readonly ManualLogSource ModLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);

        public void Awake()
        {
            Instance = this;
            ModConfig.Bind(Config);
            RegisterRoutedRpcHandlers();

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            ValheimEnforcerKickAlerts.TryPatch(_harmony);
            _configWatcher = new ConfigWatcher(Config, ModGUID, ModLogger);
            ModLogger.LogInfo($"{ModName} {ModVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
            _configWatcher?.Dispose();
            Config.Save();
            Instance = null;
        }

        private void Update()
        {
            RunUpdateStep("vendor item progress", VendorItemsPerPlayer.Update);
            RunUpdateStep("pickable ownership handoff", PickableOwnershipHandoff.Update);
            RunUpdateStep("tree ownership handoff", TreeOwnershipHandoff.Update);
        }

        private void RunUpdateStep(string name, Action update)
        {
            try
            {
                update();
            }
            catch (Exception ex)
            {
                if (ShouldLogUpdateFailure(name))
                {
                    ModLogger.LogWarning($"serverSideTweaks {name} update failed; continuing server update loop: {ex}");
                }
            }
        }

        private bool ShouldLogUpdateFailure(string name)
        {
            DateTime now = DateTime.UtcNow;
            if (_lastUpdateFailureLogTimes.TryGetValue(name, out DateTime lastLog) &&
                (now - lastLog).TotalSeconds < UpdateFailureLogIntervalSeconds)
            {
                return false;
            }

            _lastUpdateFailureLogTimes[name] = now;
            return true;
        }

        private static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Clear();
            PerPlayerLocationIcons.ClearRuntimeCache();
            VendorItemsPerPlayer.ClearRuntimeCache();
            BossMessage.RegisterRoutedRpcHandlers();
            DoorOwnershipHandoff.RegisterRoutedRpcHandlers();
            MineRockOwnershipHandoff.RegisterRoutedRpcHandlers();
            HarvestOwnershipHandoff.RegisterRoutedRpcHandlers();
            FermenterOwnershipHandoff.RegisterRoutedRpcHandlers();
            TreeOwnershipHandoff.RegisterRoutedRpcHandlers();
            PickableOwnershipHandoff.RegisterRoutedRpcHandlers();
        }
    }
} 
