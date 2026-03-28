using Microsoft.Extensions.Logging;
using VortexWin.Core.Config;
using VortexWin.Core.Helpers;

namespace VortexWin.Service.Engine;

/// <summary>
/// Monitors the Desktop for sentinel folder creation using FileSystemWatcher
/// with 1-second polling fallback for reliability.
/// Single instance enforced. Disposed after success or on service stop.
/// </summary>
public sealed class DesktopWatcher : IDisposable
{
    private readonly ILogger<DesktopWatcher> _logger;
    private readonly ConfigManager _configManager;
    private readonly SentinelManager _sentinelManager;
    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _pollingTimer;

    private readonly object _lock = new();
    private bool _isWatching;
    private string _desktopPath = string.Empty;

    /// <summary>
    /// Raised when the sentinel folder is detected on Desktop.
    /// </summary>
    public event EventHandler<string>? SentinelDetected;

    public DesktopWatcher(
        ILogger<DesktopWatcher> logger,
        ConfigManager configManager,
        SentinelManager sentinelManager)
    {
        _logger = logger;
        _configManager = configManager;
        _sentinelManager = sentinelManager;
    }

    /// <summary>
    /// Start watching the Desktop for sentinel folder creation.
    /// Uses FileSystemWatcher + 1-second polling for reliability.
    /// </summary>
    public void StartWatching()
    {
        lock (_lock)
        {
            if (_isWatching)
            {
                _logger.LogWarning("Desktop watcher already active.");
                return;
            }

            _desktopPath = DesktopPathResolver.GetDesktopPath();

            if (!Directory.Exists(_desktopPath))
            {
                _logger.LogError("Desktop path does not exist: {Path}. Cannot start watcher.", _desktopPath);
                return;
            }

            _logger.LogInformation("Starting desktop watcher on: {Path}", _desktopPath);

            // FileSystemWatcher for Created event
            try
            {
                _watcher = new FileSystemWatcher(_desktopPath)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _watcher.Created += OnFolderCreated;
                _watcher.Renamed += OnFolderRenamed;
                _watcher.Error += OnWatcherError;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create FileSystemWatcher.");
            }

            // PRD Requirement: Robust 1-second fallback polling with try/catch
            _pollingTimer = new System.Timers.Timer(1000);
            _pollingTimer.Elapsed += OnPollTick;
            _pollingTimer.AutoReset = true;
            _pollingTimer.Start();

            _isWatching = true;
        }
    }

    /// <summary>
    /// Stop watching and dispose all resources.
    /// </summary>
    public void StopWatching()
    {
        lock (_lock)
        {
            if (!_isWatching) return;

            _isWatching = false;

            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFolderCreated;
                _watcher.Renamed -= OnFolderRenamed;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
                _watcher = null;
            }

            if (_pollingTimer is not null)
            {
                _pollingTimer.Stop();
                _pollingTimer.Elapsed -= OnPollTick;
                _pollingTimer.Dispose();
                _pollingTimer = null;
            }

            _logger.LogInformation("Desktop watcher stopped.");
        }
    }

    /// <summary>
    /// FileSystemWatcher Created event handler.
    /// </summary>
    private void OnFolderCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            _logger.LogDebug("Folder created event: {Name}", e.Name);
            CheckFolder(e.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing desktop folder event.");
        }
    }

    /// <summary>
    /// Robust polling fallback — checks if sentinel folder exists every 1 second.
    /// Safely catches any IO exceptions to prevent background crash.
    /// </summary>
    private void OnPollTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            if (!_isWatching) return;

            string sentinelName = _configManager.GetSentinelName();
            string sentinelPath = Path.Combine(_desktopPath, sentinelName);

            if (Directory.Exists(sentinelPath))
            {
                CheckFolder(sentinelPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Transient error during 1-second Desktop polling check.");
        }
    }

    /// <summary>
    /// FileSystemWatcher Renamed event handler (to catch renames to sentinel name).
    /// </summary>
    private void OnFolderRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            _logger.LogDebug("Folder renamed event: {OldName} → {NewName}", e.OldName, e.Name);
            CheckFolder(e.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling folder renamed event.");
        }
    }


    /// <summary>
    /// Verify if a folder matches the sentinel (name + optional ADS tag).
    /// </summary>
    private void CheckFolder(string folderPath)
    {
        lock (_lock)
        {
            if (!_isWatching) return;

            if (_sentinelManager.Verify(folderPath))
            {
                _logger.LogInformation("Sentinel folder verified: {Path}", folderPath);
                StopWatching();
                SentinelDetected?.Invoke(this, folderPath);
            }
            else
            {
                var config = _configManager.Load();
                if (config.Advanced.PenaltyTimerEnabled)
                {
                    string folderName = Path.GetFileName(folderPath);
                    string sentinelName = _configManager.GetSentinelName();

                    // Check if it's a close match (case-insensitive name match but no ADS)
                    if (string.Equals(folderName, sentinelName, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Folder name matches but ADS verification failed. Penalty may apply.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Handle FileSystemWatcher errors (buffer overflow, etc.)
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error. Attempting restart...");

        // Restart watcher
        lock (_lock)
        {
            if (_isWatching)
            {
                StopWatching();
                StartWatching();
            }
        }
    }

    public void Dispose()
    {
        StopWatching();
    }
}
