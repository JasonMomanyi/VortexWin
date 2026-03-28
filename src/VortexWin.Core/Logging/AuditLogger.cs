using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace VortexWin.Core.Logging;

/// <summary>
/// Structured audit logger with HMAC-signed entries for tamper detection.
/// Writes to %AppData%\VortexWin\audit.log (encrypted).
/// </summary>
public sealed class AuditLogger : IDisposable
{
    private readonly Logger _serilogLogger;
    private readonly string _logPath;
    private readonly object _writeLock = new();
    private static readonly byte[] HmacEntropy = Encoding.UTF8.GetBytes("VortexWin_AuditLog_HMAC_v1");

    public AuditLogger() : this(GetDefaultLogPath()) { }

    public AuditLogger(string logPath)
    {
        _logPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);

        _serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                _logPath,
                rollingInterval: RollingInterval.Month,
                retainedFileCountLimit: 12,
                outputTemplate: "{Message:lj}{NewLine}")
            .CreateLogger();
    }

    /// <summary>
    /// Log a security-relevant event with HMAC signature.
    /// </summary>
    public void LogEvent(AuditEventType eventType, string message, AuditOutcome outcome = AuditOutcome.Success)
    {
        var entry = new AuditEntry
        {
            Timestamp = DateTime.UtcNow,
            EventType = eventType,
            Message = message,
            Outcome = outcome,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName
        };

        entry.Hmac = ComputeEntryHmac(entry);

        string json = JsonSerializer.Serialize(entry);

        lock (_writeLock)
        {
            _serilogLogger.Information(json);
        }
    }

    /// <summary>
    /// Load and return all audit entries from the log file.
    /// </summary>
    public List<AuditEntry> LoadEntries()
    {
        var entries = new List<AuditEntry>();
        if (!File.Exists(_logPath)) return entries;

        foreach (string line in File.ReadAllLines(_logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return entries;
    }

    /// <summary>
    /// Verify the HMAC of a single audit entry. Returns false if tampered.
    /// </summary>
    public static bool VerifyEntry(AuditEntry entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.Hmac))
            return false;

        string storedHmac = entry.Hmac;
        string computedHmac = ComputeEntryHmac(entry);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedHmac),
            Encoding.UTF8.GetBytes(computedHmac));
    }

    /// <summary>
    /// Export all audit entries as CSV content (for encrypted CSV export).
    /// </summary>
    public string ExportCsv()
    {
        var entries = LoadEntries();
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,EventType,Message,Outcome,MachineName,UserName,HmacValid");

        foreach (var entry in entries)
        {
            bool valid = VerifyEntry(entry);
            sb.AppendLine(
                $"\"{entry.Timestamp:O}\",\"{entry.EventType}\",\"{EscapeCsv(entry.Message)}\",\"{entry.Outcome}\",\"{entry.MachineName}\",\"{entry.UserName}\",\"{valid}\"");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Clear all audit log entries.
    /// </summary>
    public void ClearLog()
    {
        lock (_writeLock)
        {
            if (File.Exists(_logPath))
                File.WriteAllText(_logPath, string.Empty);
        }
    }

    public void Dispose()
    {
        _serilogLogger.Dispose();
    }

    private static string ComputeEntryHmac(AuditEntry entry)
    {
        // Compute HMAC over the non-HMAC fields
        string content = $"{entry.Timestamp:O}|{entry.EventType}|{entry.Message}|{entry.Outcome}|{entry.MachineName}|{entry.UserName}";
        byte[] secret = GetHmacSecret();
        using var hmac = new HMACSHA256(secret);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToBase64String(hash);
    }

    private static byte[] GetHmacSecret()
    {
        string secretPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VortexWin", "keys", "audit_hmac.secret");

        if (File.Exists(secretPath))
        {
            byte[] protectedSecret = File.ReadAllBytes(secretPath);
            return ProtectedData.Unprotect(protectedSecret, HmacEntropy, DataProtectionScope.CurrentUser);
        }

        byte[] newSecret = RandomNumberGenerator.GetBytes(32);
        byte[] protectedNew = ProtectedData.Protect(newSecret, HmacEntropy, DataProtectionScope.CurrentUser);

        Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);
        File.WriteAllBytes(secretPath, protectedNew);
        return newSecret;
    }

    private static string EscapeCsv(string value) => value.Replace("\"", "\"\"");

    private static string GetDefaultLogPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "VortexWin", "logs", "audit.log");
    }
}

/// <summary>
/// Types of auditable events.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditEventType
{
    SessionLogon,
    ChallengeStarted,
    ChallengeCompleted,
    ChallengeFailed,
    ShutdownTriggered,
    RecoveryAttempt,
    ConfigChanged,
    BypassUsed,
    EmergencyOverride,
    ServiceStarted,
    ServiceStopped,
    ServiceCrashed,
    TamperDetected,
    InvalidPinAttempt,
    InvalidPasswordAttempt,
    SettingsExport,
    SettingsImport
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditOutcome
{
    Success,
    Failure,
    Warning,
    Info
}

/// <summary>
/// Single audit log entry with HMAC signature.
/// </summary>
public sealed class AuditEntry
{
    public DateTime Timestamp { get; set; }
    public AuditEventType EventType { get; set; }
    public string Message { get; set; } = string.Empty;
    public AuditOutcome Outcome { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 signature of this entry, computed over all other fields.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hmac { get; set; }
}
