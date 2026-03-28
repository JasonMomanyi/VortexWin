using Microsoft.Extensions.Logging;

namespace VortexWin.Service.Failsafe;

/// <summary>
/// Generates and manages the failsafe batch script that triggers shutdown
/// if the Vortex Win service is killed before completing its challenge.
/// Placed in: %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\vortex_failsafe.bat
/// </summary>
public static class FailsafeInstaller
{
    private const string FailsafeFileName = "vortex_failsafe.bat";

    /// <summary>
    /// Generate and install the failsafe batch script.
    /// </summary>
    public static void Install(ILogger? logger = null)
    {
        try
        {
            string startupPath = GetStartupPath();
            string batPath = Path.Combine(startupPath, FailsafeFileName);

            string content = """
                @echo off
                REM ──────────────────────────────────────────────────
                REM  Vortex Win Failsafe Script
                REM  This script runs on logon. If the Vortex Win
                REM  service is not running, it forces a shutdown
                REM  after 5 minutes as a safety fallback.
                REM ──────────────────────────────────────────────────

                REM Check if VortexWin service is running
                sc query VortexWinService | find "RUNNING" >nul 2>&1
                if %errorlevel% equ 0 (
                    REM Service is running, no failsafe needed
                    exit /b 0
                )

                REM Service is NOT running — engage failsafe
                echo [Vortex Win] Service not detected. Failsafe shutdown in 300 seconds.
                shutdown /s /t 300 /c "Vortex Win failsafe: Service not running. Shutting down in 5 minutes."
                """;

            Directory.CreateDirectory(startupPath);
            File.WriteAllText(batPath, content);

            logger?.LogInformation("Failsafe script installed: {Path}", batPath);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to install failsafe script.");
        }
    }

    /// <summary>
    /// Remove the failsafe batch script.
    /// </summary>
    public static void Uninstall(ILogger? logger = null)
    {
        try
        {
            string batPath = Path.Combine(GetStartupPath(), FailsafeFileName);
            if (File.Exists(batPath))
            {
                File.Delete(batPath);
                logger?.LogInformation("Failsafe script removed: {Path}", batPath);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to uninstall failsafe script.");
        }
    }

    /// <summary>
    /// Check if the failsafe script is currently installed.
    /// </summary>
    public static bool IsInstalled()
    {
        string batPath = Path.Combine(GetStartupPath(), FailsafeFileName);
        return File.Exists(batPath);
    }

    private static string GetStartupPath()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    }
}
