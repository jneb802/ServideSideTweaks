using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ServideSideTweaks.Features.Chat;
using ServideSideTweaks.Features.Pickables;
using ServideSideTweaks.Features.Trees;

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
            ResetDataFile.Update();
            PickableOwnershipHandoff.Update();
            TreeOwnershipHandoff.Update();
        }
    }
} 
