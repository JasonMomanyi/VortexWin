using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using VortexWin.Core.Config;
using VortexWin.Core.Crypto;
using VortexWin.Core.Ipc;
using VortexWin.Core.Logging;
using VortexWin.Service.Engine;

namespace VortexWin.Service.Ipc;

/// <summary>
/// Named Pipe IPC server running in the service process.
/// Listens for commands from Tray and Settings apps.
/// ACL restricted to current user SID.
/// </summary>
public sealed class IpcServer
{
    private readonly ILogger<IpcServer> _logger;
    private readonly ConfigManager _configManager;
    private readonly AuditLogger _auditLogger;
    private readonly TimerEngine _timerEngine;
    private readonly SentinelManager _sentinelManager;
    private bool _running;

    public IpcServer(
        ILogger<IpcServer> logger,
        ConfigManager configManager,
        AuditLogger auditLogger,
        TimerEngine timerEngine,
        SentinelManager sentinelManager)
    {
        _logger = logger;
        _configManager = configManager;
        _auditLogger = auditLogger;
        _timerEngine = timerEngine;
        _sentinelManager = sentinelManager;
    }

    /// <summary>
    /// Start listening for IPC connections.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _running = true;
        _logger.LogInformation("IPC server starting on pipe: {Pipe}", IpcConstants.PipeName);

        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                // Create pipe with ACL restricted to current user
                var pipeSecurity = CreatePipeSecurity();
                var pipe = NamedPipeServerStreamAcl.Create(
                    IpcConstants.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: IpcConstants.MaxMessageSize,
                    outBufferSize: IpcConstants.MaxMessageSize,
                    pipeSecurity);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // Handle connection in background
                _ = Task.Run(() => HandleConnectionAsync(pipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IPC server error.");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("IPC server stopped.");
    }

    /// <summary>
    /// Stop the IPC server.
    /// </summary>
    public void Stop()
    {
        _running = false;
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            {
                // Read length-prefixed message
                byte[] lengthBytes = new byte[4];
                int bytesRead = await pipe.ReadAsync(lengthBytes, ct).ConfigureAwait(false);
                if (bytesRead < 4) return;

                int messageLength = BitConverter.ToInt32(lengthBytes, 0);
                if (messageLength <= 0 || messageLength > IpcConstants.MaxMessageSize) return;

                byte[] messageBytes = new byte[messageLength];
                int totalRead = 0;
                while (totalRead < messageLength)
                {
                    int read = await pipe.ReadAsync(
                        messageBytes.AsMemory(totalRead, messageLength - totalRead), ct)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    totalRead += read;
                }

                string json = Encoding.UTF8.GetString(messageBytes, 0, totalRead);
                var request = IpcRequest.Deserialize(json);
                if (request is null) return;

                // Process command
                var response = ProcessCommand(request);

                // Write response
                string responseJson = response.Serialize();
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
                byte[] responseLengthBytes = BitConverter.GetBytes(responseBytes.Length);

                await pipe.WriteAsync(responseLengthBytes, ct).ConfigureAwait(false);
                await pipe.WriteAsync(responseBytes, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling IPC connection.");
        }
    }

    private IpcResponse ProcessCommand(IpcRequest request)
    {
        _logger.LogDebug("IPC command received: {Command}", request.Command);

        return request.Command switch
        {
            IpcCommand.QueryStatus => HandleQueryStatus(),
            IpcCommand.GetCountdown => HandleGetCountdown(),
            IpcCommand.GetConfig => HandleGetConfig(),
            IpcCommand.PauseTimer => HandlePauseTimer(),
            IpcCommand.ResumeTimer => HandleResumeTimer(),
            IpcCommand.UpdateConfig => HandleUpdateConfig(request),
            IpcCommand.VerifyRecoveryPin => HandleVerifyPin(request),
            IpcCommand.VerifyMasterPassword => HandleVerifyMasterPassword(request),
            IpcCommand.VerifyBypassKey => HandleVerifyBypassKey(request),
            IpcCommand.RevealSentinelName => HandleRevealSentinelName(request),
            IpcCommand.EmergencyOverride => HandleEmergencyOverride(request),
            _ => new IpcResponse { Status = IpcStatus.InvalidRequest, Message = "Unknown command." }
        };
    }

    private IpcResponse HandleQueryStatus()
    {
        var config = _configManager.Load();
        var status = new ServiceStatusDto
        {
            State = GetCurrentState(),
            RemainingSeconds = _timerEngine.RemainingSeconds,
            TotalSeconds = _timerEngine.TotalSeconds,
            SentinelExists = CheckSentinelExists(),
            HintPhrase = config.Sentinel.HintPhrase
        };

        return new IpcResponse
        {
            Status = IpcStatus.Ok,
            Data = status.Serialize()
        };
    }

    private IpcResponse HandleGetCountdown()
    {
        return new IpcResponse
        {
            Status = IpcStatus.Ok,
            Data = _timerEngine.RemainingSeconds.ToString()
        };
    }

    private IpcResponse HandleGetConfig()
    {
        var config = _configManager.Load();
        return new IpcResponse
        {
            Status = IpcStatus.Ok,
            Data = System.Text.Json.JsonSerializer.Serialize(config)
        };
    }

    private IpcResponse HandlePauseTimer()
    {
        _timerEngine.Pause();
        _auditLogger.LogEvent(AuditEventType.ConfigChanged, "Timer paused via IPC.");
        return new IpcResponse { Status = IpcStatus.Ok, Message = "Timer paused." };
    }

    private IpcResponse HandleResumeTimer()
    {
        _timerEngine.Resume();
        _auditLogger.LogEvent(AuditEventType.ConfigChanged, "Timer resumed via IPC.");
        return new IpcResponse { Status = IpcStatus.Ok, Message = "Timer resumed." };
    }

    private IpcResponse HandleUpdateConfig(IpcRequest request)
    {
        if (string.IsNullOrEmpty(request.Payload))
            return new IpcResponse { Status = IpcStatus.InvalidRequest, Message = "No config data." };

        try
        {
            var newConfig = System.Text.Json.JsonSerializer.Deserialize<VortexConfig>(request.Payload);
            if (newConfig is null)
                return new IpcResponse { Status = IpcStatus.Error, Message = "Invalid config data." };

            _configManager.Save(newConfig);
            _auditLogger.LogEvent(AuditEventType.ConfigChanged, "Configuration updated via IPC.");
            return new IpcResponse { Status = IpcStatus.Ok, Message = "Config updated." };
        }
        catch (Exception ex)
        {
            return new IpcResponse { Status = IpcStatus.Error, Message = ex.Message };
        }
    }

    private IpcResponse HandleVerifyPin(IpcRequest request)
    {
        if (string.IsNullOrEmpty(request.Payload))
            return new IpcResponse { Status = IpcStatus.InvalidRequest };

        var config = _configManager.Load();
        bool valid = HashingService.VerifyPin(request.Payload, config.Security.RecoveryPinHash);

        _auditLogger.LogEvent(
            valid ? AuditEventType.RecoveryAttempt : AuditEventType.InvalidPinAttempt,
            $"Recovery PIN verification: {(valid ? "success" : "failed")}",
            valid ? AuditOutcome.Success : AuditOutcome.Failure);

        return new IpcResponse
        {
            Status = valid ? IpcStatus.Ok : IpcStatus.Unauthorized,
            Message = valid ? "PIN verified." : "Invalid PIN."
        };
    }

    private IpcResponse HandleVerifyMasterPassword(IpcRequest request)
    {
        if (string.IsNullOrEmpty(request.Payload))
            return new IpcResponse { Status = IpcStatus.InvalidRequest };

        var config = _configManager.Load();
        bool valid = HashingService.VerifyMasterPassword(request.Payload, config.Security.MasterPasswordHash);

        _auditLogger.LogEvent(
            valid ? AuditEventType.RecoveryAttempt : AuditEventType.InvalidPasswordAttempt,
            $"Master password verification: {(valid ? "success" : "failed")}",
            valid ? AuditOutcome.Success : AuditOutcome.Failure);

        return new IpcResponse
        {
            Status = valid ? IpcStatus.Ok : IpcStatus.Unauthorized,
            Message = valid ? "Password verified." : "Invalid password."
        };
    }

    private IpcResponse HandleVerifyBypassKey(IpcRequest request)
    {
        if (string.IsNullOrEmpty(request.Payload))
            return new IpcResponse { Status = IpcStatus.InvalidRequest };

        var config = _configManager.Load();
        bool valid = HashingService.VerifyBypassKey(request.Payload, config.Security.BypassKeyHmac);

        _auditLogger.LogEvent(
            AuditEventType.BypassUsed,
            $"Bypass key verification: {(valid ? "success" : "failed")}",
            valid ? AuditOutcome.Success : AuditOutcome.Failure);

        return new IpcResponse
        {
            Status = valid ? IpcStatus.Ok : IpcStatus.Unauthorized,
            Message = valid ? "Bypass key verified." : "Invalid bypass key."
        };
    }

    private IpcResponse HandleRevealSentinelName(IpcRequest request)
    {
        // Requires PIN verification first (PIN should be in AuthToken)
        if (string.IsNullOrEmpty(request.AuthToken))
            return new IpcResponse { Status = IpcStatus.Unauthorized, Message = "PIN required." };

        var config = _configManager.Load();
        bool pinValid = HashingService.VerifyPin(request.AuthToken, config.Security.RecoveryPinHash);

        if (!pinValid)
        {
            _auditLogger.LogEvent(AuditEventType.InvalidPinAttempt, "Failed PIN for sentinel reveal.", AuditOutcome.Failure);
            return new IpcResponse { Status = IpcStatus.Unauthorized, Message = "Invalid PIN." };
        }

        string name = _configManager.GetSentinelName();
        _auditLogger.LogEvent(AuditEventType.RecoveryAttempt, "Sentinel name revealed via PIN.", AuditOutcome.Success);

        return new IpcResponse
        {
            Status = IpcStatus.Ok,
            Data = name
        };
    }

    private IpcResponse HandleEmergencyOverride(IpcRequest request)
    {
        if (string.IsNullOrEmpty(request.Payload))
            return new IpcResponse { Status = IpcStatus.InvalidRequest, Message = "Master password required." };

        var config = _configManager.Load();
        bool valid = HashingService.VerifyMasterPassword(request.Payload, config.Security.MasterPasswordHash);

        if (!valid)
        {
            _auditLogger.LogEvent(AuditEventType.EmergencyOverride, "Emergency override failed — invalid password.", AuditOutcome.Failure);
            return new IpcResponse { Status = IpcStatus.Unauthorized, Message = "Invalid master password." };
        }

        // Override: stop timer, cancel challenge
        _timerEngine.Stop();
        _auditLogger.LogEvent(AuditEventType.EmergencyOverride, "Emergency override activated — challenge bypassed.", AuditOutcome.Warning);

        return new IpcResponse { Status = IpcStatus.Ok, Message = "Emergency override activated. Challenge bypassed for this session." };
    }

    private ServiceState GetCurrentState()
    {
        if (_timerEngine.IsRunning) return ServiceState.CountdownActive;
        if (_timerEngine.IsPaused) return ServiceState.Paused;
        if (CheckSentinelExists()) return ServiceState.Secured;
        return ServiceState.Disabled;
    }

    private bool CheckSentinelExists()
    {
        try
        {
            string desktop = Core.Helpers.DesktopPathResolver.GetDesktopPath();
            string sentinelName = _configManager.GetSentinelName();
            return Directory.Exists(Path.Combine(desktop, sentinelName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Create a PipeSecurity ACL restricting access to the current user.
    /// </summary>
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();

        // Allow current user full control
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        // Also allow SYSTEM (service runs as SYSTEM)
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(
            systemSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Allow administrators
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(
            adminSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return security;
    }
}
