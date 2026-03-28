using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VortexWin.Core.Config;

namespace VortexWin.Service.Engine;

/// <summary>
/// Executes system shutdown via Win32 InitiateSystemShutdownEx API.
/// Supports Shutdown, Hibernate, and Lock Screen modes.
/// Has a dry-run mode for testing.
/// </summary>
public sealed class ShutdownExecutor
{
    private readonly ILogger<ShutdownExecutor>? _logger;
    private readonly bool _dryRun;

    private const string ShutdownReason = "Vortex Security unLock Authentication Failed";

    public ShutdownExecutor(bool dryRun)
    {
        _dryRun = dryRun;
    }

    public ShutdownExecutor(bool dryRun, ILogger<ShutdownExecutor> logger) : this(dryRun)
    {
        _logger = logger;
    }

    /// <summary>
    /// Execute the configured failure action.
    /// </summary>
    public void Execute(ShutdownAction action)
    {
        if (_dryRun)
        {
            _logger?.LogWarning("[DRY RUN] Would execute: {Action}", action);
            return;
        }

        _logger?.LogCritical("Executing failure action: {Action}", action);

        switch (action)
        {
            case ShutdownAction.Shutdown:
                ExecuteShutdown();
                break;
            case ShutdownAction.Hibernate:
                ExecuteHibernate();
                break;
            case ShutdownAction.LockScreen:
                ExecuteLockScreen();
                break;
        }
    }

    /// <summary>
    /// Register a shutdown block reason (call during PreShutdown to ensure cleanup completes).
    /// </summary>
    public static void RegisterShutdownBlock(IntPtr hWnd, string reason)
    {
        ShutdownBlockReasonCreate(hWnd, reason);
    }

    /// <summary>
    /// Unregister the shutdown block reason.
    /// </summary>
    public static void UnregisterShutdownBlock(IntPtr hWnd)
    {
        ShutdownBlockReasonDestroy(hWnd);
    }

    private void ExecuteShutdown()
    {
        _logger?.LogCritical("Initiating system shutdown...");

        // Enable SE_SHUTDOWN_NAME privilege
        EnableShutdownPrivilege();

        bool result = InitiateSystemShutdownEx(
            null,                  // machine name (null = local)
            ShutdownReason,        // message
            10,                    // timeout in seconds
            true,                 // force apps closed
            false,                // reboot (false = shutdown)
            SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_MINOR_OTHER | SHTDN_REASON_FLAG_PLANNED);

        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            _logger?.LogError("InitiateSystemShutdownEx failed with error code: {Error}", error);

            // Fallback: use shutdown.exe
            FallbackShutdown();
        }
    }

    private void ExecuteHibernate()
    {
        _logger?.LogWarning("Initiating system hibernate...");
        SetSuspendState(true, true, false);
    }

    private static void ExecuteLockScreen()
    {
        LockWorkStation();
    }

    private void FallbackShutdown()
    {
        _logger?.LogWarning("Using fallback shutdown via shutdown.exe...");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /t 10 /c \"Vortex Security Challenge Failed\" /f",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Fallback shutdown also failed.");
        }
    }

    private static void EnableShutdownPrivilege()
    {
        try
        {
            var tokenHandle = IntPtr.Zero;
            OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tokenHandle);

            var luid = new LUID();
            LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, ref luid);

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            CloseHandle(tokenHandle);
        }
        catch
        {
            // If privilege elevation fails, shutdown may still work if running as SYSTEM
        }
    }

    // ── Win32 P/Invoke Declarations ──

    private const uint SHTDN_REASON_MAJOR_OTHER = 0x00000000;
    private const uint SHTDN_REASON_MINOR_OTHER = 0x00000000;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InitiateSystemShutdownEx(
        string? lpMachineName,
        string lpMessage,
        uint dwTimeout,
        bool bForceAppsClosed,
        bool bRebootAfterShutdown,
        uint dwReason);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string reason);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, ref LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }
}
