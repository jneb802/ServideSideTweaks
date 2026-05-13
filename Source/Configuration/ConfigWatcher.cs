using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace ServerSideTweaks
{
    internal sealed class ConfigWatcher : IDisposable
    {
        private const long ReloadDelayTicks = 10000000;

        private readonly ConfigFile _config;
        private readonly string _configFileFullPath;
        private readonly string _configFileName;
        private readonly ManualLogSource _logger;
        private readonly FileSystemWatcher _watcher;
        private DateTime _lastReloadTime;

        internal ConfigWatcher(ConfigFile config, string modGuid, ManualLogSource logger)
        {
            _config = config;
            _configFileName = modGuid + ".cfg";
            _configFileFullPath = Path.Combine(Paths.ConfigPath, _configFileName);
            _logger = logger;
            _lastReloadTime = DateTime.Now;

            _watcher = new FileSystemWatcher(Paths.ConfigPath, _configFileName)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            _watcher.Changed += ReadConfigValues;
            _watcher.Created += ReadConfigValues;
            _watcher.Renamed += ReadConfigValues;
        }

        public void Dispose()
        {
            _watcher.Changed -= ReadConfigValues;
            _watcher.Created -= ReadConfigValues;
            _watcher.Renamed -= ReadConfigValues;
            _watcher.Dispose();
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            DateTime now = DateTime.Now;
            long time = now.Ticks - _lastReloadTime.Ticks;
            if (!File.Exists(_configFileFullPath) || time < ReloadDelayTicks)
            {
                return;
            }

            try
            {
                _logger.LogInfo("Attempting to reload configuration...");
                _config.Reload();
                _logger.LogInfo("Configuration reloaded successfully.");
            }
            catch
            {
                _logger.LogError($"There was an issue loading {_configFileName}");
                return;
            }

            _lastReloadTime = now;
        }
    }
}
