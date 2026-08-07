using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Services;

/// <summary>
/// AES-256-GCM token protector. The 256-bit key is derived from <see cref="BlocwerkSettings.EncryptionKey"/>
/// via SHA-256 (so any passphrase works). Output is base64 of <c>nonce(12) | tag(16) | ciphertext</c>.
/// </summary>
public sealed class TokenProtector : ITokenProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? key;

    public TokenProtector(BlocwerkSettings settings)
    {
        key = string.IsNullOrEmpty(settings.EncryptionKey)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(settings.EncryptionKey));
    }

    public bool IsConfigured => key is not null;

    public string Protect(string plaintext)
    {
        var k = Require();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(k, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var output = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, output, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string ciphertext)
    {
        var k = Require();
        var raw = Convert.FromBase64String(ciphertext);
        if (raw.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext is too short to be valid.");
        }

        var nonce = raw.AsSpan(0, NonceSize);
        var tag = raw.AsSpan(NonceSize, TagSize);
        var cipher = raw.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(k, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] Require() =>
        key ?? throw new InvalidOperationException(
            "No encryption key configured (BLOCWERK__ENCRYPTIONKEY); cannot protect stored tokens.");
}
