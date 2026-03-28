using Microsoft.Win32;

namespace VortexWin.Core.Helpers;

/// <summary>
/// Resolves the active Desktop path, accounting for OneDrive Desktop redirection.
/// </summary>
public static class DesktopPathResolver
{
    private const string OneDriveRegKey = @"SOFTWARE\Microsoft\OneDrive";
    private const string OneDriveUserFolderKey = @"SOFTWARE\Microsoft\OneDrive\Accounts\Personal";

    /// <summary>
    /// Gets the active Desktop path. Resolves OneDrive Desktop redirect if present.
    /// Falls back to %USERPROFILE%\Desktop.
    /// </summary>
    public static string GetDesktopPath()
    {
        // First, try the Shell Folder registry path (most reliable)
        string? shellDesktop = GetShellFolderDesktopPath();
        if (!string.IsNullOrEmpty(shellDesktop) && Directory.Exists(shellDesktop))
            return shellDesktop;

        // Try OneDrive redirect
        string? oneDriveDesktop = GetOneDriveDesktopPath();
        if (!string.IsNullOrEmpty(oneDriveDesktop) && Directory.Exists(oneDriveDesktop))
            return oneDriveDesktop;

        // Fallback to standard Desktop via Environment
        string standardDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(standardDesktop) && Directory.Exists(standardDesktop))
            return standardDesktop;

        // Last resort: construct manually
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Desktop");
    }

    /// <summary>
    /// Check if OneDrive Desktop redirect is active.
    /// </summary>
    public static bool IsOneDriveRedirectActive()
    {
        string? oneDriveDesktop = GetOneDriveDesktopPath();
        if (string.IsNullOrEmpty(oneDriveDesktop))
            return false;

        string standardDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return !string.Equals(oneDriveDesktop, standardDesktop, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(oneDriveDesktop);
    }

    /// <summary>
    /// Get all possible Desktop paths (standard + OneDrive) for comprehensive monitoring.
    /// </summary>
    public static string[] GetAllDesktopPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string primary = GetDesktopPath();
        paths.Add(primary);

        string standard = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(standard) && Directory.Exists(standard))
            paths.Add(standard);

        string? oneDrive = GetOneDriveDesktopPath();
        if (!string.IsNullOrEmpty(oneDrive) && Directory.Exists(oneDrive))
            paths.Add(oneDrive);

        return [.. paths];
    }

    private static string? GetShellFolderDesktopPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            string? desktopPath = key?.GetValue("Desktop") as string;
            if (!string.IsNullOrEmpty(desktopPath))
            {
                // Expand environment variables (e.g., %USERPROFILE%)
                return Environment.ExpandEnvironmentVariables(desktopPath);
            }
        }
        catch
        {
            // Registry access may fail — ignore
        }

        return null;
    }

    private static string? GetOneDriveDesktopPath()
    {
        try
        {
            // Check for OneDrive folder path
            using var key = Registry.CurrentUser.OpenSubKey(OneDriveUserFolderKey);
            string? oneDrivePath = key?.GetValue("UserFolder") as string;

            if (!string.IsNullOrEmpty(oneDrivePath))
            {
                string desktopPath = Path.Combine(oneDrivePath, "Desktop");
                if (Directory.Exists(desktopPath))
                    return desktopPath;
            }

            // Alternative: HKCU\Environment\OneDrive + Desktop
            using var envKey = Registry.CurrentUser.OpenSubKey(@"Environment");
            string? oneDriveRoot = envKey?.GetValue("OneDrive") as string;
            if (!string.IsNullOrEmpty(oneDriveRoot))
            {
                string desktopPath = Path.Combine(oneDriveRoot, "Desktop");
                if (Directory.Exists(desktopPath))
                    return desktopPath;
            }
        }
        catch
        {
            // Registry access may fail — ignore
        }

        return null;
    }
}
