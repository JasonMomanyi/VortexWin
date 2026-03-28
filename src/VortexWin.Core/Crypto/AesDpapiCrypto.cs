using System.Security.Cryptography;
using System.Text;

namespace VortexWin.Core.Crypto;

/// <summary>
/// AES-256-CBC encryption with DPAPI-protected key storage.
/// Used for sentinel folder name, config file encryption, and other secrets.
/// Key material is derived via DPAPI with machine-bound entropy so it is
/// tied to the current user on this machine.
/// </summary>
public sealed class AesDpapiCrypto
{
    private const int KeySize = 32;  // AES-256
    private const int IvSize = 16;   // AES block size
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VortexWin_AES_Entropy_v1");

    /// <summary>
    /// Encrypts plaintext using AES-256-CBC with a DPAPI-protected key.
    /// Returns Base64-encoded ciphertext with prepended IV.
    /// </summary>
    public static string Encrypt(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        byte[] key = GetOrCreateKey();
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        // Prepend IV to ciphertext
        byte[] result = new byte[IvSize + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, IvSize);
        Buffer.BlockCopy(cipherBytes, 0, result, IvSize, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypts a Base64-encoded ciphertext previously encrypted with <see cref="Encrypt"/>.
    /// </summary>
    public static string Decrypt(string cipherBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(cipherBase64);

        byte[] key = GetOrCreateKey();
        byte[] combined = Convert.FromBase64String(cipherBase64);

        if (combined.Length < IvSize + 1)
            throw new CryptographicException("Ciphertext is too short to contain IV and data.");

        byte[] iv = new byte[IvSize];
        Buffer.BlockCopy(combined, 0, iv, 0, IvSize);

        byte[] cipherBytes = new byte[combined.Length - IvSize];
        Buffer.BlockCopy(combined, IvSize, cipherBytes, 0, cipherBytes.Length);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        byte[] plaintextBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    /// <summary>
    /// Gets or creates the AES key, persisted via DPAPI in the VortexWin data folder.
    /// </summary>
    private static byte[] GetOrCreateKey()
    {
        string keyPath = GetKeyFilePath();
        if (File.Exists(keyPath))
        {
            byte[] protectedKey = File.ReadAllBytes(keyPath);
            return ProtectedData.Unprotect(protectedKey, Entropy, DataProtectionScope.CurrentUser);
        }

        // Generate new key and protect it with DPAPI
        byte[] newKey = RandomNumberGenerator.GetBytes(KeySize);
        byte[] protectedNewKey = ProtectedData.Protect(newKey, Entropy, DataProtectionScope.CurrentUser);

        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, protectedNewKey);

        return newKey;
    }

    /// <summary>
    /// Path to the DPAPI-protected AES key file.
    /// </summary>
    private static string GetKeyFilePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "VortexWin", "keys", "aes.key");
    }
}
