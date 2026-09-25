// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Mints and checks runner keys: <c>bwr_&lt;64 hex&gt;</c> (32 random bytes). Only the SHA-256 is
/// stored; the lookup is by hash, and the found row's hash is compared again in constant time.
/// </summary>
public static class GpuRunnerTokens
{
    private const int PrefixLength = 12;
    private const int TokenLength = 4 + 64;

    public static (string Token, string Prefix) Create()
    {
        var token = GpuRunner.TokenPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return (token, token[..PrefixLength]);
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>True when the value is shaped like a runner key (prefix, length, lowercase hex).</summary>
    public static bool LooksLikeRunnerKey(string? value) =>
        value is { Length: TokenLength } && value.StartsWith(GpuRunner.TokenPrefix, StringComparison.Ordinal)
        && value.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>Constant-time comparison of two hex hashes.</summary>
    public static bool HashEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
