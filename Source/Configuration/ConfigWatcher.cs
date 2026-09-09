using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace ServerSideTweaks
{
    internal sealed class ConfigWatcher : IDisposable
    {
        private const int ReloadDelayMilliseconds = 1000;

        private readonly ConfigFile _config;
        private readonly string _configFileFullPath;
        private readonly string _configFileName;
        private readonly ManualLogSource _logger;
        private readonly object _stateLock = new object();
        private readonly FileSystemWatcher _watcher;
        private DateTime _reloadNotBeforeUtc;
        private bool _reloadPending;
        private bool _disposed;

        internal ConfigWatcher(ConfigFile config, string modGuid, ManualLogSource logger)
        {
            _config = config;
            _configFileName = modGuid + ".cfg";
            _configFileFullPath = Path.Combine(Paths.ConfigPath, _configFileName);
            _logger = logger;

            _watcher = new FileSystemWatcher(Paths.ConfigPath, _configFileName)
            {
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Changed += ScheduleReload;
            _watcher.Created += ScheduleReload;
            _watcher.Renamed += ScheduleReload;
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                _disposed = true;
                _reloadPending = false;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= ScheduleReload;
            _watcher.Created -= ScheduleReload;
            _watcher.Renamed -= ScheduleReload;
            _watcher.Dispose();
        }

        internal void Update()
        {
            lock (_stateLock)
            {
                if (_disposed || !_reloadPending || DateTime.UtcNow < _reloadNotBeforeUtc)
                {
                    return;
                }

                _reloadPending = false;
            }

            ReloadConfig();
        }

        private void ScheduleReload(object sender, FileSystemEventArgs e)
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _reloadPending = true;
                _reloadNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(ReloadDelayMilliseconds);
            }
        }

        private void ReloadConfig()
        {
            if (!File.Exists(_configFileFullPath))
            {
                return;
            }

            bool saveOnConfigSet = _config.SaveOnConfigSet;
            try
            {
                _logger.LogInfo("Attempting to reload configuration...");
                _config.SaveOnConfigSet = false;
                _config.Reload();
                _logger.LogInfo("Configuration reloaded successfully.");
            }
            catch (Exception exception)
            {
                _logger.LogError($"There was an issue loading {_configFileName}: {exception}");
            }
            finally
            {
                _config.SaveOnConfigSet = saveOnConfigSet;
            }
        }
    }
}
