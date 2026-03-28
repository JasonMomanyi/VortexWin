using Microsoft.Extensions.Logging;
using VortexWin.Core.Config;
using VortexWin.Core.Helpers;

namespace VortexWin.Service.Engine;

/// <summary>
/// Manages sentinel folder lifecycle: primer creation, verification, deletion, icon change.
/// </summary>
public sealed class SentinelManager
{
    private readonly ILogger<SentinelManager> _logger;
    private readonly ConfigManager _configManager;
    private string? _cachedSessionSuffix;

    public SentinelManager(ILogger<SentinelManager> logger, ConfigManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
    }

    /// <summary>
    /// Create the sentinel folder on Desktop as a primer for the challenge.
    /// Writes ADS verification tag if enabled.
    /// </summary>
    public void CreatePrimer()
    {
        try
        {
            string desktopPath = DesktopPathResolver.GetDesktopPath();
            string sentinelName = GetEffectiveSentinelName();
            string fullPath = Path.Combine(desktopPath, sentinelName);

            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
                _logger.LogInformation("Sentinel primer created: {Path}", fullPath);
            }

            // Write ADS verification tag
            var config = _configManager.Load();
            if (config.Sentinel.AdsVerificationEnabled)
            {
                bool tagWritten = NtfsAdsHelper.WriteVerificationTag(fullPath);
                if (tagWritten)
                    _logger.LogInformation("ADS verification tag written to sentinel folder.");
                else
                    _logger.LogWarning("Failed to write ADS verification tag.");
            }

            // Create secondary verification file if enabled
            if (config.Sentinel.SecondaryVerificationEnabled &&
                !string.IsNullOrEmpty(config.Sentinel.SecondaryFileName))
            {
                string filePath = Path.Combine(fullPath, config.Sentinel.SecondaryFileName);
                if (!File.Exists(filePath))
                {
                    File.WriteAllText(filePath, "VortexWin verification file");
                    _logger.LogInformation("Secondary verification file created: {File}", config.Sentinel.SecondaryFileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sentinel primer.");
        }
    }

    /// <summary>
    /// Delete the sentinel folder from Desktop.
    /// If renamed/moved, search by ADS tag.
    /// </summary>
    public void DeleteSentinel()
    {
        try
        {
            string desktopPath = DesktopPathResolver.GetDesktopPath();
            string sentinelName = GetEffectiveSentinelName();
            string fullPath = Path.Combine(desktopPath, sentinelName);

            // Try direct path first
            if (Directory.Exists(fullPath))
            {
                NtfsAdsHelper.DeleteFolderWithAds(fullPath);
                _logger.LogInformation("Sentinel folder deleted: {Path}", fullPath);
                return;
            }

            // If not found by name, search by ADS tag (handles rename/move)
            _logger.LogWarning("Sentinel not found at expected path. Searching by ADS tag...");
            string? taggedFolder = NtfsAdsHelper.FindTaggedFolder(desktopPath);
            if (taggedFolder is not null)
            {
                NtfsAdsHelper.DeleteFolderWithAds(taggedFolder);
                _logger.LogInformation("Sentinel folder found by ADS tag and deleted: {Path}", taggedFolder);
            }
            else
            {
                _logger.LogInformation("No sentinel folder found on Desktop to delete.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete sentinel folder.");
        }
    }

    /// <summary>
    /// Verify if a folder is the valid sentinel (name match + ADS tag if enabled).
    /// Case-insensitive name comparison.
    /// </summary>
    public bool Verify(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return false;

        string folderName = Path.GetFileName(folderPath);
        string sentinelName = GetEffectiveSentinelName();

        // Case-insensitive name match
        if (!string.Equals(folderName, sentinelName, StringComparison.OrdinalIgnoreCase))
            return false;

        var config = _configManager.Load();

        // ADS verification if enabled
        if (config.Sentinel.AdsVerificationEnabled)
        {
            if (!NtfsAdsHelper.HasVerificationTag(folderPath))
            {
                _logger.LogWarning("Folder name matches but ADS tag missing/invalid: {Path}", folderPath);
                return false;
            }
        }

        // Secondary verification if enabled
        if (config.Sentinel.SecondaryVerificationEnabled &&
            !string.IsNullOrEmpty(config.Sentinel.SecondaryFileName))
        {
            string expectedFile = Path.Combine(folderPath, config.Sentinel.SecondaryFileName);
            if (!File.Exists(expectedFile))
            {
                _logger.LogWarning("Sentinel folder missing secondary verification file: {File}",
                    config.Sentinel.SecondaryFileName);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Optionally set the sentinel folder icon to a padlock via desktop.ini.
    /// </summary>
    public void SetPadlockIcon(string folderPath)
    {
        try
        {
            string iniPath = Path.Combine(folderPath, "desktop.ini");

            string iniContent = """
                [.ShellClassInfo]
                IconResource=C:\Windows\System32\shell32.dll,47
                [ViewState]
                Mode=
                Vid=
                FolderType=Generic
                """;

            File.WriteAllText(iniPath, iniContent);
            File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(folderPath,
                File.GetAttributes(folderPath) | FileAttributes.System);

            _logger.LogInformation("Padlock icon set on sentinel folder.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set padlock icon on sentinel folder.");
        }
    }

    /// <summary>
    /// Get the effective sentinel name, including random suffix if enabled.
    /// </summary>
    private string GetEffectiveSentinelName()
    {
        string baseName = _configManager.GetSentinelName();
        var config = _configManager.Load();

        if (config.Sentinel.RandomSuffixEnabled)
        {
            // Generate a deterministic suffix per session and cache it so it doesn't change mid-session
            _cachedSessionSuffix ??= GenerateSessionSuffix();
            return $"{baseName}_{_cachedSessionSuffix}";
        }

        return baseName;
    }

    /// <summary>
    /// Generate a 4-char random suffix that stays consistent per boot session.
    /// Uses the boot time as seed for determinism within a session.
    /// </summary>
    private static string GenerateSessionSuffix()
    {
        // Use system uptime as seed — consistent within a session, changes on reboot
        long uptickMs = Environment.TickCount64;
        long seed = uptickMs / 60000; // Round to minutes for slight tolerance
        var rng = new Random((int)(seed & 0x7FFFFFFF));

        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        char[] suffix = new char[4];
        for (int i = 0; i < 4; i++)
        {
            suffix[i] = chars[rng.Next(chars.Length)];
        }
        return new string(suffix);
    }
}
