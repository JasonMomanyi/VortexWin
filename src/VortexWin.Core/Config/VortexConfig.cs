using System.Text.Json.Serialization;

namespace VortexWin.Core.Config;

/// <summary>
/// Root configuration object for the entire Vortex Win application.
/// Persisted as AES-256 encrypted JSON via DPAPI.
/// </summary>
public sealed class VortexConfig
{
    public SentinelConfig Sentinel { get; set; } = new();
    public TimerConfig Timer { get; set; } = new();
    public AlertConfig Alerts { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public AppearanceConfig Appearance { get; set; } = new();
    public AdvancedConfig Advanced { get; set; } = new();
    public string ConfigVersion { get; set; } = "1.0.0";
}

/// <summary>
/// Sentinel folder configuration.
/// The Name is stored encrypted — never plaintext on disk.
/// </summary>
public sealed class SentinelConfig
{
    /// <summary>Encrypted sentinel folder name. Default: "VortexProof"</summary>
    public string EncryptedName { get; set; } = string.Empty;

    /// <summary>Plaintext hint phrase shown in alerts — NOT the folder name.</summary>
    public string HintPhrase { get; set; } = "Contains the word Proof";

    /// <summary>Whether to append a random 4-char suffix each session.</summary>
    public bool RandomSuffixEnabled { get; set; } = false;

    /// <summary>Whether to write NTFS ADS verification tag.</summary>
    public bool AdsVerificationEnabled { get; set; } = true;

    /// <summary>Whether to require a specific file inside the sentinel folder.</summary>
    public bool SecondaryVerificationEnabled { get; set; } = false;

    /// <summary>Filename required inside sentinel folder when secondary verification is on.</summary>
    public string SecondaryFileName { get; set; } = string.Empty;
}

/// <summary>
/// Timer configuration for the grace period countdown.
/// </summary>
public sealed class TimerConfig
{
    /// <summary>Grace period duration in seconds. Default: 90. Range: 10–14400.</summary>
    public int DurationSeconds { get; set; } = 90;

    /// <summary>Whether to only trigger on cold boot (Event ID 6005 vs 6006).</summary>
    public bool ColdBootOnly { get; set; } = false;

    /// <summary>Delay in seconds after logon before starting timer. Default: 8.</summary>
    public int StartupDelaySeconds { get; set; } = 8;
}

/// <summary>
/// Alert threshold configuration.
/// </summary>
public sealed class AlertConfig
{
    public bool GlobalEnabled { get; set; } = true;
    public AlertThreshold Threshold75 { get; set; } = new()
    {
        Percent = 75,
        Title = "Vortex Win: Active",
        Body = "Complete your session routine.",
        SoundType = AlertSound.Silent,
        Position = AlertPosition.TopRight,
        AutoDismissSeconds = 5
    };
    public AlertThreshold Threshold50 { get; set; } = new()
    {
        Percent = 50,
        Title = "Vortex Win: Reminder",
        Body = "Vortex Win: Reminder — {HINT}. Time remaining: {TIME}.",
        SoundType = AlertSound.Chime,
        Position = AlertPosition.TopRight,
        AutoDismissSeconds = 10
    };
    public AlertThreshold Threshold25 { get; set; } = new()
    {
        Percent = 25,
        Title = "Vortex Win: URGENT",
        Body = "Vortex Win: URGENT — {HINT}. Shutdown in {TIME} seconds.",
        SoundType = AlertSound.LoudBeep,
        Position = AlertPosition.Center,
        AutoDismissSeconds = 0, // persistent
        ShowForgotButton = true
    };
    public AlertThreshold ThresholdExpiry { get; set; } = new()
    {
        Percent = 0,
        Title = "Vortex Win: SHUTTING DOWN",
        Body = "SHUTTING DOWN in 10 seconds. Ritual not detected.",
        SoundType = AlertSound.LoudBeep,
        Position = AlertPosition.Center,
        AutoDismissSeconds = 0, // persistent
        ShowForgotButton = true,
        ShowEmergencyOverride = true
    };
}

public sealed class AlertThreshold
{
    public int Percent { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public AlertSound SoundType { get; set; } = AlertSound.Silent;
    public string CustomSoundPath { get; set; } = string.Empty;
    public AlertPosition Position { get; set; } = AlertPosition.Center;
    public int AutoDismissSeconds { get; set; } = 10;
    public bool ShowForgotButton { get; set; } = false;
    public bool ShowEmergencyOverride { get; set; } = false;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertSound
{
    Silent,
    SystemBeep,
    Chime,
    LoudBeep,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertPosition
{
    Center,
    TopRight,
    BottomRight
}

/// <summary>
/// Security credentials (all hashed, never plaintext).
/// </summary>
public sealed class SecurityConfig
{
    /// <summary>bcrypt hash of the recovery PIN (4–8 digits).</summary>
    public string RecoveryPinHash { get; set; } = string.Empty;

    /// <summary>Argon2id hash of the master password.</summary>
    public string MasterPasswordHash { get; set; } = string.Empty;

    /// <summary>SHA-256 HMAC of the emergency bypass key.</summary>
    public string BypassKeyHmac { get; set; } = string.Empty;

    /// <summary>Whether first-run setup wizard has been completed.</summary>
    public bool SetupCompleted { get; set; } = false;

    /// <summary>Parental PIN bcrypt hash (optional).</summary>
    public string ParentalPinHash { get; set; } = string.Empty;
}

/// <summary>
/// Appearance settings.
/// </summary>
public sealed class AppearanceConfig
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string AccentColorOverride { get; set; } = string.Empty;
    public int FontSize { get; set; } = 14;
    public TrayIconStyle TrayStyle { get; set; } = TrayIconStyle.Default;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppTheme
{
    Light,
    Dark,
    System
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrayIconStyle
{
    Default,
    Minimal,
    Hidden
}

/// <summary>
/// Advanced feature toggles (all off by default).
/// </summary>
public sealed class AdvancedConfig
{
    public bool StealthMode { get; set; } = false;
    public bool DecoyModeEnabled { get; set; } = false;
    public int DecoyCount { get; set; } = 3;
    public bool PenaltyTimerEnabled { get; set; } = false;
    public bool ScheduledBypassEnabled { get; set; } = false;
    public List<BypassWindow> BypassWindows { get; set; } = [];
    public bool MultiProfileEnabled { get; set; } = false;
    public bool DynamicHintsEnabled { get; set; } = false;
    public List<string> DynamicHints { get; set; } = [];
    public bool FolderIconChangeEnabled { get; set; } = false;
    public bool AutoUpdateEnabled { get; set; } = false;
    public bool SelfRestartEnabled { get; set; } = false;
    public int SelfRestartIntervalHours { get; set; } = 24;

    /// <summary>Configurable action on timer expiry.</summary>
    public ShutdownAction FailureAction { get; set; } = ShutdownAction.Shutdown;
}

public sealed class BypassWindow
{
    public DayOfWeek Day { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShutdownAction
{
    Shutdown,
    Hibernate,
    LockScreen
}
