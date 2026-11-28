// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Text;

namespace Blocwerk.Core.DeviceLinks;

/// <summary>The PKCE (RFC 7636, S256 only) and code helpers of the device link.</summary>
public static class DeviceLinkPkce
{
    /// <summary>The length of an S256 challenge and of every code this class mints: 32 bytes in URL-safe base64.</summary>
    public const int EncodedLength = 43;

    /// <summary>RFC 7636 bounds for a code verifier.</summary>
    public const int MinVerifierLength = 43;

    /// <inheritdoc cref="MinVerifierLength"/>
    public const int MaxVerifierLength = 128;

    /// <summary>A fresh one-time code.</summary>
    public static string CreateCode() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Hex SHA-256 of a code, as stored on the row.</summary>
    public static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    /// <summary>The S256 challenge of a verifier: URL-safe base64 of its SHA-256, without padding.</summary>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// The short code the app and the consent page both show, so the user can tell a link they started from one someone
    /// sent them: the top 30 bits of SHA-256 over the challenge text, as six Crockford base32 characters, "XXX-XXX".
    /// </summary>
    public static string MatchCode(string challenge)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(challenge));
        var bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) >> 2;
        var chars = new char[7];
        for (int i = 0, at = 0; i < 6; i++, at++)
        {
            if (i == 3)
            {
                chars[at++] = '-';
            }

            chars[at] = alphabet[(int)((bits >> (25 - (5 * i))) & 31)];
        }

        return new string(chars);
    }

    /// <summary>True when <paramref name="verifier"/> is RFC 7636 shaped and hashes to <paramref name="challenge"/>.</summary>
    public static bool Verifies(string? verifier, string challenge)
    {
        if (!IsWellFormedVerifier(verifier))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Challenge(verifier!)),
            Encoding.ASCII.GetBytes(challenge));
    }

    /// <summary>True when the value has the shape of an S256 challenge (and of a code this class mints).</summary>
    public static bool IsWellFormedChallenge(string? value) => IsUrlSafe(value, EncodedLength, EncodedLength);

    /// <summary>RFC 7636: 43 to 128 unreserved characters (letters, digits, <c>-._~</c>).</summary>
    public static bool IsWellFormedVerifier(string? verifier)
    {
        if (verifier is null || verifier.Length is < MinVerifierLength or > MaxVerifierLength)
        {
            return false;
        }

        foreach (var c in verifier)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the value is <paramref name="min"/>..<paramref name="max"/> URL-safe base64 characters.</summary>
    public static bool IsUrlSafe(string? value, int min, int max)
    {
        if (value is null || value.Length < min || value.Length > max)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
