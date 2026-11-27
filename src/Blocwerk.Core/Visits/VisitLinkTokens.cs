// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Text;

namespace Blocwerk.Core.Visits;

/// <summary>
/// Mints and hashes visitor-link tokens: 32 random bytes (256 bits) in URL-safe base64, 43 characters. Only the hash
/// is ever stored (<see cref="Entities.WallVisitLink.TokenHash"/>), the same discipline as <c>ApiKeyTokens</c>.
/// </summary>
public static class VisitLinkTokens
{
    /// <summary>The length of every token this class mints.</summary>
    public const int Length = 43;

    /// <summary>A fresh token.</summary>
    public static string Create() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Hex SHA-256 of a token, as stored on the row.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// True when <paramref name="token"/> has the shape of a minted token. Anything else is refused before it reaches
    /// the database, so a stray path segment costs no lookup.
    /// </summary>
    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != Length)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
