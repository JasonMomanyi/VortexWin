using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VortexWin.Core.Crypto;

namespace VortexWin.Core.Config;

/// <summary>
/// Manages loading and saving the encrypted VortexConfig JSON file.
/// Config path: %AppData%\VortexWin\config.vx
/// Integrity hash: %AppData%\VortexWin\config.vx.hash
/// </summary>
public sealed class ConfigManager
{
    private static readonly object _lock = new();
    private VortexConfig? _cachedConfig;

    public string ConfigPath { get; }
    public string HashPath { get; }

    public ConfigManager() : this(GetDefaultConfigPath()) { }

    public ConfigManager(string configPath)
    {
        ConfigPath = configPath;
        HashPath = configPath + ".hash";
    }

    /// <summary>
    /// Load the config from disk, decrypt, verify integrity, and return.
    /// Returns default config if file doesn't exist.
    /// Throws if tamper detected.
    /// </summary>
    public VortexConfig Load()
    {
        lock (_lock)
        {
            if (_cachedConfig is not null)
                return _cachedConfig;

            if (!File.Exists(ConfigPath))
            {
                _cachedConfig = CreateDefaultConfig();
                return _cachedConfig;
            }

            string encryptedContent = File.ReadAllText(ConfigPath, Encoding.UTF8);

            // Verify integrity
            if (File.Exists(HashPath))
            {
                string storedHash = File.ReadAllText(HashPath, Encoding.UTF8).Trim();
                string computedHash = ComputeHash(encryptedContent);
                if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(storedHash),
                    Encoding.UTF8.GetBytes(computedHash)))
                {
                    throw new InvalidOperationException(
                        "Config file integrity check failed — possible tampering detected.");
                }
            }

            string json = AesDpapiCrypto.Decrypt(encryptedContent);
            _cachedConfig = JsonSerializer.Deserialize<VortexConfig>(json) ?? CreateDefaultConfig();
            return _cachedConfig;
        }
    }

    /// <summary>
    /// Encrypt and save the config to disk with integrity hash.
    /// </summary>
    public void Save(VortexConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_lock)
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            string encrypted = AesDpapiCrypto.Encrypt(json);

            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, encrypted, Encoding.UTF8);

            // Write integrity hash
            string hash = ComputeHash(encrypted);
            File.WriteAllText(HashPath, hash, Encoding.UTF8);

            _cachedConfig = config;
        }
    }

    /// <summary>
    /// Clear the in-memory cached config to force re-read from disk.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_lock)
        {
            _cachedConfig = null;
        }
    }

    /// <summary>
    /// Get the decrypted sentinel folder name from config.
    /// </summary>
    public string GetSentinelName()
    {
        var config = Load();
        if (string.IsNullOrEmpty(config.Sentinel.EncryptedName))
            return "VortexProof";

        return AesDpapiCrypto.Decrypt(config.Sentinel.EncryptedName);
    }

    /// <summary>
    /// Set the sentinel folder name — encrypts before storing.
    /// </summary>
    public void SetSentinelName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var config = Load();
        config.Sentinel.EncryptedName = AesDpapiCrypto.Encrypt(name);
        Save(config);
    }

    /// <summary>
    /// Create a fresh default config with encrypted default sentinel name.
    /// </summary>
    private static VortexConfig CreateDefaultConfig()
    {
        var config = new VortexConfig();
        config.Sentinel.EncryptedName = AesDpapiCrypto.Encrypt("VortexProof");
        return config;
    }

    /// <summary>
    /// Compute SHA-256 hash of config content for integrity verification.
    /// </summary>
    private static string ComputeHash(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToBase64String(hash);
    }

    private static string GetDefaultConfigPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "VortexWin", "config.vx");
    }
}
