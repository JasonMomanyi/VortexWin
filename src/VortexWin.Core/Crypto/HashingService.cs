using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace VortexWin.Core.Crypto;

/// <summary>
/// Industry-standard hashing for each secret type:
/// - Recovery PIN: bcrypt (cost 12)
/// - Master Password: Argon2id
/// - Emergency Bypass Key: SHA-256 HMAC with machine-bound secret
/// </summary>
public static class HashingService
{
    private const int BcryptWorkFactor = 12;
    private const int Argon2Parallelism = 4;
    private const int Argon2MemoryKB = 65536; // 64 MB
    private const int Argon2Iterations = 3;
    private const int Argon2HashLength = 32;
    private const int BypassKeyLength = 24;

    // ── Recovery PIN (bcrypt) ──────────────────────────────────

    /// <summary>Hash a 4–8 digit recovery PIN using bcrypt with cost 12.</summary>
    public static string HashPin(string pin)
    {
        ValidatePin(pin);
        return BCrypt.Net.BCrypt.HashPassword(pin, BCrypt.Net.BCrypt.GenerateSalt(BcryptWorkFactor));
    }

    /// <summary>Verify a PIN against its bcrypt hash.</summary>
    public static bool VerifyPin(string pin, string hash)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(hash))
            return false;
        return BCrypt.Net.BCrypt.Verify(pin, hash);
    }

    // ── Master Password (Argon2id) ─────────────────────────────

    /// <summary>Hash the master password using Argon2id with high-memory config.</summary>
    public static string HashMasterPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = ComputeArgon2(password, salt);

        // Store as salt:hash in Base64
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>Verify a password against its Argon2id hash.</summary>
    public static bool VerifyMasterPassword(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
            return false;

        string[] parts = storedHash.Split(':');
        if (parts.Length != 2) return false;

        byte[] salt = Convert.FromBase64String(parts[0]);
        byte[] expectedHash = Convert.FromBase64String(parts[1]);
        byte[] actualHash = ComputeArgon2(password, salt);

        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    // ── Emergency Bypass Key (SHA-256 HMAC) ────────────────────

    /// <summary>
    /// Generate a random 24-character alphanumeric bypass key.
    /// This is shown to the user ONCE and never stored in plaintext.
    /// </summary>
    public static string GenerateBypassKey()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        char[] key = new char[BypassKeyLength];
        for (int i = 0; i < BypassKeyLength; i++)
        {
            key[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }
        return new string(key);
    }

    /// <summary>
    /// Compute SHA-256 HMAC of the bypass key using a machine-bound secret (DPAPI).
    /// </summary>
    public static string HashBypassKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        byte[] secret = GetMachineSecret();
        using var hmac = new HMACSHA256(secret);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hash);
    }

    /// <summary>Verify a bypass key against its HMAC hash.</summary>
    public static bool VerifyBypassKey(string key, string storedHmac)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(storedHmac))
            return false;

        string computedHmac = HashBypassKey(key);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(storedHmac),
            Convert.FromBase64String(computedHmac));
    }

    // ── Parental PIN (bcrypt, same as recovery) ────────────────

    /// <summary>Hash a parental PIN using bcrypt with cost 12.</summary>
    public static string HashParentalPin(string pin) => HashPin(pin);

    /// <summary>Verify a parental PIN against its bcrypt hash.</summary>
    public static bool VerifyParentalPin(string pin, string hash) => VerifyPin(pin, hash);

    // ── Private Helpers ────────────────────────────────────────

    private static byte[] ComputeArgon2(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = Argon2Parallelism,
            MemorySize = Argon2MemoryKB,
            Iterations = Argon2Iterations
        };
        return argon2.GetBytes(Argon2HashLength);
    }

    private static void ValidatePin(string pin)
    {
        ArgumentException.ThrowIfNullOrEmpty(pin);
        if (pin.Length < 4 || pin.Length > 8)
            throw new ArgumentException("PIN must be 4–8 digits.", nameof(pin));
        if (!pin.All(char.IsDigit))
            throw new ArgumentException("PIN must contain only digits.", nameof(pin));
    }

    /// <summary>
    /// Machine-bound secret for HMAC operations, stored via DPAPI.
    /// </summary>
    private static byte[] GetMachineSecret()
    {
        string secretPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VortexWin", "keys", "hmac.secret");

        if (File.Exists(secretPath))
        {
            byte[] protectedSecret = File.ReadAllBytes(secretPath);
            return System.Security.Cryptography.ProtectedData.Unprotect(
                protectedSecret,
                Encoding.UTF8.GetBytes("VortexWin_HMAC_Entropy_v1"),
                DataProtectionScope.CurrentUser);
        }

        byte[] newSecret = RandomNumberGenerator.GetBytes(32);
        byte[] protectedNew = System.Security.Cryptography.ProtectedData.Protect(
            newSecret,
            Encoding.UTF8.GetBytes("VortexWin_HMAC_Entropy_v1"),
            DataProtectionScope.CurrentUser);

        Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);
        File.WriteAllBytes(secretPath, protectedNew);

        return newSecret;
    }
}
