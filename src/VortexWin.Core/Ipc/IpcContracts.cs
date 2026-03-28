using System.Text.Json;
using System.Text.Json.Serialization;

namespace VortexWin.Core.Ipc;

/// <summary>
/// Named Pipe IPC message contracts between Service, Tray, and Settings processes.
/// Pipe name: \\.\pipe\VortexWinIPC
/// </summary>
public static class IpcConstants
{
    public const string PipeName = "VortexWinIPC";
    public const string TrayPipeName = "VortexWinTrayIPC";
    public const int ConnectionTimeoutMs = 5000;
    public const int MaxMessageSize = 65536;
}

/// <summary>
/// Command types the Tray/Settings apps can send to the Service.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpcCommand
{
    // Status queries
    QueryStatus,
    GetCountdown,
    GetConfig,

    // Timer control
    PauseTimer,
    ResumeTimer,
    ResetTimer,

    // Challenge
    TestChallenge,
    CancelTestChallenge,

    // Config updates
    UpdateConfig,
    UpdateSentinelName,
    UpdateTimerDuration,

    // Recovery
    RevealSentinelName,
    VerifyRecoveryPin,
    VerifyMasterPassword,
    VerifyBypassKey,
    EmergencyOverride,

    // Service control
    DisableService,
    EnableService,
    RestartService,

    // Alert
    ShowAlert,
    DismissAlert
}

/// <summary>
/// Status codes for IPC responses.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpcStatus
{
    Ok,
    Error,
    Unauthorized,
    InvalidRequest,
    Timeout
}

/// <summary>
/// Current operational state of the Vortex Win service.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceState
{
    /// <summary>Sentinel detected, timer not running.</summary>
    Secured,

    /// <summary>Timer is counting down, waiting for sentinel.</summary>
    CountdownActive,

    /// <summary>Timer is paused.</summary>
    Paused,

    /// <summary>Service is disabled.</summary>
    Disabled,

    /// <summary>Alert sequence has fired, shutdown imminent.</summary>
    AlertActive,

    /// <summary>Test challenge in progress (dry-run).</summary>
    TestMode,

    /// <summary>Service is starting up.</summary>
    Initializing
}

/// <summary>
/// Request sent from Tray/Settings to Service via Named Pipe.
/// </summary>
public sealed class IpcRequest
{
    public IpcCommand Command { get; set; }
    public string? Payload { get; set; }
    public string? AuthToken { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);
    public static IpcRequest? Deserialize(string json) => JsonSerializer.Deserialize<IpcRequest>(json);
}

/// <summary>
/// Response sent from Service back to Tray/Settings via Named Pipe.
/// </summary>
public sealed class IpcResponse
{
    public IpcStatus Status { get; set; }
    public string? Message { get; set; }
    public string? Data { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);
    public static IpcResponse? Deserialize(string json) => JsonSerializer.Deserialize<IpcResponse>(json);
}

/// <summary>
/// Status snapshot returned in response to QueryStatus.
/// </summary>
public sealed class ServiceStatusDto
{
    public ServiceState State { get; set; }
    public int RemainingSeconds { get; set; }
    public int TotalSeconds { get; set; }
    public DateTime? LastChallengeTime { get; set; }
    public bool SentinelExists { get; set; }
    public string HintPhrase { get; set; } = string.Empty;

    public string Serialize() => JsonSerializer.Serialize(this);
    public static ServiceStatusDto? Deserialize(string json) => JsonSerializer.Deserialize<ServiceStatusDto>(json);
}

public sealed class AlertData
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int PercentRemaining { get; set; }
    public int SecondsRemaining { get; set; }
    
    public VortexWin.Core.Config.AlertSound SoundType { get; set; }
    public string CustomSoundPath { get; set; } = string.Empty;
    public VortexWin.Core.Config.AlertPosition Position { get; set; }
    
    public int AutoDismissSeconds { get; set; }
    public bool ShowForgotButton { get; set; }
    public bool ShowEmergencyOverride { get; set; }
    public bool IsExpiry { get; set; }
    public bool IsSecured { get; set; }
}
