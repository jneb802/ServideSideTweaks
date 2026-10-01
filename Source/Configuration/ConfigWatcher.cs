using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace ServerSideTweaks
{
    internal sealed class ConfigWatcher
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        private readonly ConfigFile _config;
        private readonly ManualLogSource _logger;
        private string? _appliedContent;
        private string? _pendingContent;
        private DateTime _pendingSinceUtc;
        private DateTime _nextPollUtc;

        internal ConfigWatcher(ConfigFile config, ManualLogSource logger)
        {
            _config = config;
            _logger = logger;
            _appliedContent = ReadContentHash();
        }

        internal void Update()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextPollUtc)
            {
                return;
            }

            _nextPollUtc = now.Add(PollInterval);
            string? content = ReadContentHash();
            if (content == null || content == _appliedContent)
            {
                _pendingContent = null;
                return;
            }

            // Require a stable file across two polls so an editor can finish its write.
            // Poll contents because file watcher events were missed on the Linux profile.
            if (content != _pendingContent)
            {
                _pendingContent = content;
                _pendingSinceUtc = now;
                return;
            }

            if (now - _pendingSinceUtc < PollInterval)
            {
                return;
            }

            bool saveOnConfigSet = _config.SaveOnConfigSet;
            try
            {
                _logger.LogInfo("Attempting to reload configuration...");
                _config.SaveOnConfigSet = false;
                _config.Reload();
                _appliedContent = content;
                _pendingContent = null;
                _logger.LogInfo("Configuration reloaded successfully.");
            }
            catch (Exception exception)
            {
                _logger.LogError($"There was an issue loading {_config.ConfigFilePath}: {exception}");
            }
            finally
            {
                _config.SaveOnConfigSet = saveOnConfigSet;
            }
        }

        private string? ReadContentHash()
        {
            try
            {
                using FileStream stream = new(_config.ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using SHA256 hash = SHA256.Create();
                return Convert.ToBase64String(hash.ComputeHash(stream));
            }
            catch (IOException)
            {
                // A missing, replaced, or busy file is retried on the next poll.
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
