using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ServideSideTweaks.Features.Chat;
using ServideSideTweaks.Features.Doors;
using ServideSideTweaks.Features.Pickables;
using ServideSideTweaks.Features.Trees;
using ServideSideTweaks.Infrastructure.Routing;

namespace ServideSideTweaks
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class ServideSideTweaksPlugin : BaseUnityPlugin
    {
        private const string ModName = "Servide Side Tweaks";
        private const string ModVersion = "1.0.0";
        private const string ModGUID = "warpalicious.ServideSideTweaks";

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
            PickableOwnershipHandoff.Update();
            TreeOwnershipHandoff.Update();
        }

        private static void RegisterRoutedRpcHandlers()
        {
            RoutedRpcDispatcher.Clear();
            NormalChatToShout.RegisterRoutedRpcHandlers();
            DoorOwnershipHandoff.RegisterRoutedRpcHandlers();
            TreeOwnershipHandoff.RegisterRoutedRpcHandlers();
            PickableOwnershipHandoff.RegisterRoutedRpcHandlers();
        }
    }
} 
