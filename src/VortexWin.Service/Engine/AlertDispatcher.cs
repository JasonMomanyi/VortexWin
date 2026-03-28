using Microsoft.Extensions.Logging;
using VortexWin.Core.Config;
using VortexWin.Core.Ipc;

namespace VortexWin.Service.Engine;

/// <summary>
/// Dispatches alert commands to the Tray app via Named Pipe IPC.
/// Falls back to direct Win32 MessageBox if tray is not connected.
/// </summary>
public sealed class AlertDispatcher
{
    private readonly ILogger<AlertDispatcher> _logger;
    private readonly ConfigManager _configManager;

    public AlertDispatcher(ILogger<AlertDispatcher> logger, ConfigManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
    }

    /// <summary>
    /// Send a threshold alert to the Tray app.
    /// </summary>
    public void SendAlert(int percentRemaining, int secondsRemaining)
    {
        var config = _configManager.Load();
        if (!config.Alerts.GlobalEnabled)
        {
            _logger.LogDebug("Alerts globally disabled, skipping.");
            return;
        }

        AlertThreshold? threshold = percentRemaining switch
        {
            75 => config.Alerts.Threshold75,
            50 => config.Alerts.Threshold50,
            25 => config.Alerts.Threshold25,
            _ => null
        };

        if (threshold is null) return;

        string hint = GetCurrentHint(config);
        string body = threshold.Body
            .Replace("{HINT}", hint)
            .Replace("{TIME}", FormatTime(secondsRemaining));

        var alertData = new AlertData
        {
            Title = threshold.Title,
            Body = body,
            PercentRemaining = percentRemaining,
            SecondsRemaining = secondsRemaining,
            SoundType = threshold.SoundType,
            CustomSoundPath = threshold.CustomSoundPath,
            Position = threshold.Position,
            AutoDismissSeconds = threshold.AutoDismissSeconds,
            ShowForgotButton = threshold.ShowForgotButton,
            ShowEmergencyOverride = threshold.ShowEmergencyOverride
        };

        SendToTray(alertData);
    }

    /// <summary>
    /// Send the final expiry alert with 10-second shutdown countdown.
    /// </summary>
    public void SendExpiryAlert()
    {
        var config = _configManager.Load();
        var threshold = config.Alerts.ThresholdExpiry;
        string hint = GetCurrentHint(config);

        var alertData = new AlertData
        {
            Title = threshold.Title,
            Body = threshold.Body.Replace("{HINT}", hint),
            PercentRemaining = 0,
            SecondsRemaining = 10,
            SoundType = threshold.SoundType,
            Position = AlertPosition.Center,
            AutoDismissSeconds = 0,
            ShowForgotButton = threshold.ShowForgotButton,
            ShowEmergencyOverride = threshold.ShowEmergencyOverride,
            IsExpiry = true
        };

        SendToTray(alertData);
    }

    /// <summary>
    /// Notify the Tray app that the challenge is complete (secured status).
    /// </summary>
    public void NotifySecured()
    {
        try
        {
            using var client = new IpcClient(IpcConstants.TrayPipeName);
            var request = new IpcRequest
            {
                Command = IpcCommand.ShowAlert,
                Payload = System.Text.Json.JsonSerializer.Serialize(new AlertData
                {
                    Title = "Vortex Win: Secured",
                    Body = "Challenge completed successfully.",
                    IsSecured = true,
                    AutoDismissSeconds = 5
                })
            };
            _ = client.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not notify tray of secured status (tray may not be running).");
        }
    }

    private void SendToTray(AlertData alertData)
    {
        try
        {
            using var client = new IpcClient(IpcConstants.TrayPipeName);
            var request = new IpcRequest
            {
                Command = IpcCommand.ShowAlert,
                Payload = System.Text.Json.JsonSerializer.Serialize(alertData)
            };
            var task = client.SendAsync(request);
            task.Wait(TimeSpan.FromSeconds(2));
            _logger.LogInformation("Alert sent to tray: {Title}", alertData.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send alert to tray. Tray may not be running.");
            // Fallback: could use Win32 MessageBox, but that requires user session
        }
    }

    /// <summary>
    /// Get the current hint phrase, supporting dynamic hint rotation.
    /// </summary>
    private static string GetCurrentHint(VortexConfig config)
    {
        if (config.Advanced.DynamicHintsEnabled && config.Advanced.DynamicHints.Count > 0)
        {
            // Rotate based on day of year
            int index = DateTime.Now.DayOfYear % config.Advanced.DynamicHints.Count;
            return config.Advanced.DynamicHints[index];
        }

        return config.Sentinel.HintPhrase;
    }

    private static string FormatTime(int seconds)
    {
        if (seconds >= 3600)
            return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
        if (seconds >= 60)
            return $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds}s";
    }
}

