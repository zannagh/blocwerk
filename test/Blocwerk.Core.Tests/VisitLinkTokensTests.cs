// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Visits;

namespace Blocwerk.Core.Tests;

/// <summary>The visitor-link token: 256 random bits, URL-safe, and only its hash on the row.</summary>
public class VisitLinkTokensTests
{
    [Fact]
    public void Create_IsUrlSafeAndWellFormed()
    {
        var token = VisitLinkTokens.Create();

        Assert.Equal(VisitLinkTokens.Length, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.True(VisitLinkTokens.IsWellFormed(token));
    }

    [Fact]
    public void Create_CarriesAtLeast128BitsOfRandomness()
    {
        // 43 base64 characters hold 258 bits, of which the 32 random bytes fill 256; a thousand draws never repeat.
        var tokens = Enumerable.Range(0, 1000).Select(_ => VisitLinkTokens.Create()).ToHashSet();

        Assert.Equal(1000, tokens.Count);
        Assert.True(VisitLinkTokens.Length * 6 >= 256);
    }

    [Fact]
    public void Hash_IsTheHexSha256AndNeverTheToken()
    {
        var token = VisitLinkTokens.Create();
        var hash = VisitLinkTokens.Hash(token);

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, VisitLinkTokens.Hash(token));
        Assert.NotEqual(hash, VisitLinkTokens.Hash(VisitLinkTokens.Create()));
        Assert.DoesNotContain(token, hash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void IsWellFormed_RefusesAnythingElse(string? token)
    {
        Assert.False(VisitLinkTokens.IsWellFormed(token));
    }
}
