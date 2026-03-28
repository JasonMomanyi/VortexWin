using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VortexWin.Core.Config;
using VortexWin.Core.Logging;
using VortexWin.Service.Engine;
using VortexWin.Service.Ipc;

namespace VortexWin.Service.Workers;

/// <summary>
/// Main Vortex Win background worker. Orchestrates the entire security challenge lifecycle:
/// - Listens for session logon events
/// - Creates sentinel folder primer
/// - Starts countdown timer
/// - Monitors Desktop for sentinel folder
/// - Triggers shutdown if challenge fails
/// </summary>
public sealed class VortexWorker : BackgroundService
{
    private readonly ILogger<VortexWorker> _logger;
    private readonly ConfigManager _configManager;
    private readonly AuditLogger _auditLogger;
    private readonly TimerEngine _timerEngine;
    private readonly DesktopWatcher _desktopWatcher;
    private readonly SentinelManager _sentinelManager;
    private readonly ShutdownExecutor _shutdownExecutor;
    private readonly AlertDispatcher _alertDispatcher;
    private readonly IpcServer _ipcServer;
    private readonly IHostApplicationLifetime _lifetime;
    private IntPtr _hWnd;
    private bool _challengeActive;

    public VortexWorker(
        ILogger<VortexWorker> logger,
        ConfigManager configManager,
        AuditLogger auditLogger,
        TimerEngine timerEngine,
        DesktopWatcher desktopWatcher,
        SentinelManager sentinelManager,
        ShutdownExecutor shutdownExecutor,
        AlertDispatcher alertDispatcher,
        IpcServer ipcServer,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _configManager = configManager;
        _auditLogger = auditLogger;
        _timerEngine = timerEngine;
        _desktopWatcher = desktopWatcher;
        _sentinelManager = sentinelManager;
        _shutdownExecutor = shutdownExecutor;
        _alertDispatcher = alertDispatcher;
        _ipcServer = ipcServer;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _auditLogger.LogEvent(AuditEventType.ServiceStarted, "Vortex Win Service started.");

        // Wire up events
        _timerEngine.ThresholdReached += OnThresholdReached;
        _timerEngine.Expired += OnTimerExpired;
        _desktopWatcher.SentinelDetected += OnSentinelDetected;

        // Start IPC server in background
        _ = Task.Run(() => _ipcServer.StartAsync(stoppingToken), stoppingToken);

        // Register for session notifications
        RegisterSessionNotification();

        // Register for power/session events
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;

        // If service starts with a user already logged in, start the challenge
        // (handles case where service starts after user session is already active)
        await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        if (!stoppingToken.IsCancellationRequested)
        {
            await StartChallengeSequence(stoppingToken).ConfigureAwait(false);
        }

        // Keep running until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Start the full challenge sequence: create primer → start timer → start watcher.
    /// </summary>
    public async Task StartChallengeSequence(CancellationToken ct)
    {
        try
        {
            if (_challengeActive)
            {
                _logger.LogWarning("Challenge already active, skipping.");
                return;
            }

            var config = _configManager.Load();

            // Check cold boot only setting
            if (config.Timer.ColdBootOnly && !IsColdBoot())
            {
                _logger.LogInformation("Warm logon detected, cold-boot-only mode active. Skipping challenge.");
                return;
            }

            // Check scheduled bypass
            if (config.Advanced.ScheduledBypassEnabled && IsInBypassWindow(config))
            {
                _logger.LogInformation("In scheduled bypass window. Skipping challenge.");
                return;
            }

            _challengeActive = true;
            _logger.LogInformation("Starting challenge sequence...");
            _auditLogger.LogEvent(AuditEventType.ChallengeStarted, "Challenge sequence initiated.");

            // Wait for desktop shell to load
            int delaySeconds = config.Timer.StartupDelaySeconds;
            _logger.LogInformation("Waiting {Delay}s for desktop shell...", delaySeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _challengeActive = false;
                return;
            }

            // Create sentinel folder primer
            _sentinelManager.CreatePrimer();
            _logger.LogInformation("Sentinel primer created.");

            // Start timer
            _timerEngine.Start(config.Timer.DurationSeconds);
            _logger.LogInformation("Timer started: {Duration}s", config.Timer.DurationSeconds);

            // Start watching Desktop
            _desktopWatcher.StartWatching();
            _logger.LogInformation("Desktop watcher started.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to start challenge sequence. Fail-secure triggered.");
            _auditLogger.LogEvent(AuditEventType.ChallengeFailed, "Exception during challenge start. Fail-secure shutdown triggered.", AuditOutcome.Failure);
            
            // Fail-secure: If the challenge cannot start properly, we must lock/shutdown the system immediately
            // to prevent the computer from being unprotected.
            var config = _configManager.Load();
            _shutdownExecutor.Execute(config.Advanced.FailureAction);
        }
    }

    /// <summary>
    /// Called when the sentinel folder is detected on Desktop.
    /// </summary>
    private void OnSentinelDetected(object? sender, string folderPath)
    {
        _logger.LogInformation("Sentinel folder detected at {Path}. Challenge complete!", folderPath);
        _auditLogger.LogEvent(AuditEventType.ChallengeCompleted, $"Sentinel detected: {folderPath}");

        _timerEngine.Stop();
        _desktopWatcher.StopWatching();
        _challengeActive = false;

        // Notify tray app: secured
        _alertDispatcher.NotifySecured();

        // Optional: change folder icon to padlock
        var config = _configManager.Load();
        if (config.Advanced.FolderIconChangeEnabled)
        {
            _sentinelManager.SetPadlockIcon(folderPath);
        }

        // Cleanup
        GC.Collect();
    }

    /// <summary>
    /// Called when timer reaches a threshold (75%, 50%, 25%).
    /// </summary>
    private void OnThresholdReached(object? sender, ThresholdEventArgs e)
    {
        _logger.LogWarning("Timer threshold reached: {Percent}% time remaining.", e.PercentRemaining);
        _alertDispatcher.SendAlert(e.PercentRemaining, _timerEngine.RemainingSeconds);
    }

    /// <summary>
    /// Called when timer expires — trigger shutdown sequence.
    /// </summary>
    private void OnTimerExpired(object? sender, EventArgs e)
    {
        _logger.LogCritical("Timer expired! Triggering shutdown sequence.");
        _auditLogger.LogEvent(AuditEventType.ChallengeFailed, "Challenge failed — timer expired.");

        _desktopWatcher.StopWatching();
        _challengeActive = false;

        // Send expiry alert with 10-second countdown, then shutdown
        _alertDispatcher.SendExpiryAlert();

        // Execute shutdown after 10-second delay (built into API)
        Task.Run(async () =>
        {
            await Task.Delay(10_000).ConfigureAwait(false);
            
            _auditLogger.LogEvent(AuditEventType.ShutdownTriggered, "System shutdown executed.");

            var config = _configManager.Load();
            _shutdownExecutor.Execute(config.Advanced.FailureAction);
        });
    }

    /// <summary>
    /// Filter session switch events — only act on SessionLogon.
    /// Ignore: logoff, lock, unlock, remote connect/disconnect, fast user switch.
    /// </summary>
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        _logger.LogInformation("Session switch event: {Reason}", e.Reason);

        // Only respond to full session logon
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogon)
        {
            _logger.LogInformation("Full session logon detected. Initiating challenge.");
            _ = StartChallengeSequence(CancellationToken.None);
        }
        // All other events (logoff, lock, unlock, remote connect, fast user switch) → ignore
    }

    /// <summary>
    /// Filter power events — do nothing on sleep/hibernate/resume.
    /// </summary>
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        _logger.LogInformation("Power mode changed: {Mode}", e.Mode);
        // Explicitly do nothing — timer only starts on SessionLogon.
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Vortex Win Service stopping...");
        _auditLogger.LogEvent(AuditEventType.ServiceStopped, "Vortex Win Service stopping.");

        // Clean up on shutdown: delete sentinel folder
        try
        {
            _sentinelManager.DeleteSentinel();
            _logger.LogInformation("Sentinel folder deleted on shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete sentinel folder on shutdown.");
        }

        // Dispose watchers and timers
        _timerEngine.Stop();
        _desktopWatcher.StopWatching();
        _ipcServer.Stop();

        // Unregister events
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;

        UnregisterSessionNotification();

        _auditLogger.Dispose();

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Check if this is a cold boot by comparing Event Log entries (6005 vs 6006).
    /// </summary>
    private static bool IsColdBoot()
    {
        try
        {
            var eventLog = new System.Diagnostics.EventLog("System");
            var entries = eventLog.Entries;

            DateTime? lastStart = null;
            DateTime? lastStop = null;

            // Search recent entries for Event Log Service start (6005) and stop (6006)
            for (int i = entries.Count - 1; i >= Math.Max(0, entries.Count - 100); i--)
            {
                var entry = entries[i];
                if (entry.Source != "EventLog") continue;

                if (entry.EventID == 6005 && lastStart is null)
                    lastStart = entry.TimeGenerated;
                else if (entry.EventID == 6006 && lastStop is null)
                    lastStop = entry.TimeGenerated;

                if (lastStart.HasValue && lastStop.HasValue)
                    break;
            }

            // Cold boot = no recent 6006 (clean shutdown) before the 6005 (start)
            // Or the gap between stop and start is > 30 seconds
            if (lastStop is null) return true; // No clean shutdown record = cold boot
            if (lastStart is null) return true;

            return (lastStart.Value - lastStop.Value).TotalSeconds > 30;
        }
        catch
        {
            return true; // Default to cold boot if we can't determine
        }
    }

    private static bool IsInBypassWindow(VortexConfig config)
    {
        if (!config.Advanced.ScheduledBypassEnabled || config.Advanced.BypassWindows.Count == 0)
            return false;

        var now = DateTime.Now;
        var currentTime = TimeOnly.FromDateTime(now);
        var currentDay = now.DayOfWeek;

        return config.Advanced.BypassWindows.Any(w =>
            w.Day == currentDay && currentTime >= w.StartTime && currentTime <= w.EndTime);
    }

    // ── Session Notification Win32 ──

    private void RegisterSessionNotification()
    {
        try
        {
            // For a service, use the service status handle
            // WTSRegisterSessionNotification requires a window handle
            // In a service context, we rely on SessionSwitch events instead
            _logger.LogInformation("Session notification registered via SystemEvents.SessionSwitch.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register session notification.");
        }
    }

    private void UnregisterSessionNotification()
    {
        try
        {
            _logger.LogInformation("Session notification unregistered.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unregister session notification.");
        }
    }
}
