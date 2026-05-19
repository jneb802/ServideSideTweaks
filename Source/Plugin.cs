using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ServerSideTweaks.Features.Bosses;
using ServerSideTweaks.Features.Chat;
using ServerSideTweaks.Features.Doors;
using ServerSideTweaks.Features.Fermenters;
using ServerSideTweaks.Features.Harvest;
using ServerSideTweaks.Features.Locations;
using ServerSideTweaks.Features.Mining;
using ServerSideTweaks.Features.Pickables;
using ServerSideTweaks.Features.Trees;
using ServerSideTweaks.Features.Vendors;
using ServerSideTweaks.Infrastructure.Routing;

namespace ServerSideTweaks
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class ServerSideTweaksPlugin : BaseUnityPlugin
    {
        private const string ModName = "serverSideTweaks";
        private const string ModVersion = "1.1.5";
        private const string ModGUID = "warpalicious.serverSideTweaks";

        private readonly Harmony _harmony = new(ModGUID);
        private ConfigWatcher? _configWatcher;

        internal static readonly ManualLogSource ModLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);

        public void Awake()
        {
            ModConfig.Bind(Config);
            RegisterRoutedRpcHandlers();

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            _configWatcher = new ConfigWatcher(Config, ModGUID, ModLogger);
            ModLogger.LogInfo($"{ModName} {ModVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
            _configWatcher?.Dispose();
            Config.Save();
        }

        private void Update()
        {
            SafeUpdate("reset data file", ResetDataFile.Update);
            SafeUpdate("pickable ownership handoff", PickableOwnershipHandoff.Update);
            SafeUpdate("tree ownership handoff", TreeOwnershipHandoff.Update);
        }

        private static void SafeUpdate(string name, Action update)
        {
            try
            {
                update();
            }
            catch (Exception ex)
            {
                ModLogger.LogWarning($"serverSideTweaks {name} update failed: {ex}");
            }
        }

        private static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Clear();
            PerPlayerLocationIcons.ClearRuntimeCache();
            VendorItemsPerPlayer.ClearRuntimeCache();
            ResetChatCommands.RegisterRoutedRpcHandlers();
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
