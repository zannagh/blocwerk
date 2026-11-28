// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;

namespace Blocwerk.Authentication.Apple;

/// <summary>Loads the Sign in with Apple private key (a P-256 key in PKCS#8) from the text of its .p8 file.</summary>
public static class AppleSigningKey
{
    /// <summary>
    /// The key, or null when <paramref name="text"/> is no P-256 private key. Accepts the PEM as it is, with escaped
    /// <c>\n</c> line breaks (as some secret stores keep it), or only its base64 body.
    /// </summary>
    public static ECDsa? TryLoad(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
        var key = ECDsa.Create();
        try
        {
            if (normalized.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                key.ImportFromPem(normalized);
            }
            else
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(normalized), out _);
            }

            if (key.KeySize != 256)
            {
                key.Dispose();
                return null;
            }

            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            key.Dispose();
            return null;
        }
    }
}
