using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VortexWin.Core.Helpers;

/// <summary>
/// Manages NTFS Alternate Data Streams for sentinel folder verification.
/// ADS tag name: "vortexwin:verified"
/// Used for tamper resistance — verifies the correct folder even if renamed.
/// </summary>
public static class NtfsAdsHelper
{
    public const string AdsStreamName = "vortexwin";
    public const string AdsVerificationValue = "verified";
    private const string FullStreamSuffix = ":vortexwin";

    /// <summary>
    /// Write the vortexwin:verified ADS tag to a folder.
    /// </summary>
    public static bool WriteVerificationTag(string folderPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderPath);

        try
        {
            if (!Directory.Exists(folderPath))
                return false;

            // NTFS ADS on a folder: write to folder path + :streamname
            string adsPath = folderPath + FullStreamSuffix;
            File.WriteAllText(adsPath, AdsVerificationValue);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if a folder has the vortexwin:verified ADS tag.
    /// </summary>
    public static bool HasVerificationTag(string folderPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderPath);

        try
        {
            if (!Directory.Exists(folderPath))
                return false;

            string adsPath = folderPath + FullStreamSuffix;
            if (!File.Exists(adsPath))
                return false;

            string content = File.ReadAllText(adsPath).Trim();
            return string.Equals(content, AdsVerificationValue, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Remove the ADS verification tag from a folder.
    /// </summary>
    public static bool RemoveVerificationTag(string folderPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderPath);

        try
        {
            string adsPath = folderPath + FullStreamSuffix;

            // Delete the ADS by opening and truncating
            using var handle = CreateFileW(
                adsPath,
                GENERIC_WRITE,
                0,
                IntPtr.Zero,
                CREATE_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle.IsInvalid) return false;

            // ADS is now zero-length, effectively removed
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Search a directory for any subfolder that has the vortexwin:verified ADS tag.
    /// Used to find sentinel folder even if renamed or moved within Desktop.
    /// </summary>
    public static string? FindTaggedFolder(string searchDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(searchDirectory);

        if (!Directory.Exists(searchDirectory))
            return null;

        try
        {
            foreach (string dir in Directory.GetDirectories(searchDirectory))
            {
                if (HasVerificationTag(dir))
                    return dir;
            }
        }
        catch
        {
            // Directory enumeration may fail — ignore
        }

        return null;
    }

    /// <summary>
    /// Delete a folder and all its ADS tags.
    /// </summary>
    public static bool DeleteFolderWithAds(string folderPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderPath);

        try
        {
            RemoveVerificationTag(folderPath);

            if (Directory.Exists(folderPath))
                Directory.Delete(folderPath, recursive: true);

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Win32 Interop for ADS manipulation ──

    private const uint GENERIC_WRITE = 0x40000000;
    private const uint CREATE_ALWAYS = 2;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
