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

        private readonly Harmony _harmony = new(ModGUID);
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
            ResetDataFile.Update();
            VendorItemsPerPlayer.Update();
            PickableOwnershipHandoff.Update();
            TreeOwnershipHandoff.Update();
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
