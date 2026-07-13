using System.Security.Cryptography;
using System.Text;

namespace EggContribBot;

public sealed class SecureText {
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecureText(string keyPath) {
        var fullPath = Path.GetFullPath(keyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");

        if(File.Exists(fullPath)) {
            _key = File.ReadAllBytes(fullPath);
            if(_key.Length != KeySize) {
                throw new InvalidOperationException($"Encryption key at {fullPath} is not {KeySize} bytes.");
            }
            return;
        }

        _key = RandomNumberGenerator.GetBytes(KeySize);
        File.WriteAllBytes(fullPath, _key);
    }

    public string Encrypt(string plainText) {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using(var aes = new AesGcm(_key, TagSize)) {
            aes.Encrypt(nonce, plaintextBytes, cipherBytes, tag);
        }

        var packed = new byte[NonceSize + TagSize + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, packed, NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, packed, NonceSize + TagSize, cipherBytes.Length);
        return Convert.ToBase64String(packed);
    }

    public string Decrypt(string encryptedText) {
        var packed = Convert.FromBase64String(encryptedText);
        var nonce = packed[..NonceSize];
        var tag = packed[NonceSize..(NonceSize + TagSize)];
        var cipherBytes = packed[(NonceSize + TagSize)..];
        var plaintextBytes = new byte[cipherBytes.Length];

        using(var aes = new AesGcm(_key, TagSize)) {
            aes.Decrypt(nonce, cipherBytes, tag, plaintextBytes);
        }

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    public static string Sha256(string value) {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
