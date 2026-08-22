using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the machine-facing key lifecycle: a token is only ever handed out once, validation is a
/// hash lookup that refuses anything revoked or expired, and only wall admins can mint wall keys.
/// </summary>
public class ApiKeyTests
{
    [Fact]
    public async Task CreateWallKey_ReturnsTokenOnce_AndValidatesRoundTrip()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var (key, token) = await h.ApiKeyService.CreateWallKeyAsync(h.WallId, h.Owner.Id, "Sensor", null);

        Assert.StartsWith(ApiKey.TokenPrefix, token);
        Assert.Equal(ApiKey.TokenPrefix.Length + 64, token.Length);
        Assert.Equal(token[..12], key.Prefix);
        Assert.Equal(ApiKeyScope.Wall, key.Scope);
        Assert.Equal(h.WallId, key.WallId);

        // Only the hash is stored, so the token itself is unrecoverable afterwards.
        await using (var db = h.CreateContext())
        {
            var row = await db.ApiKeys.AsNoTracking().FirstAsync(k => k.Id == key.Id);
            Assert.DoesNotContain(token, row.KeyHash);
            Assert.Null(row.LastUsedAt);
        }

        var validated = await h.ApiKeyService.ValidateAsync(token);
        Assert.NotNull(validated);
        Assert.Equal(key.Id, validated.Id);
        Assert.NotNull(validated.LastUsedAt);
    }

    [Fact]
    public async Task Validate_RejectsUnknownRevokedAndExpiredTokens()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var (_, token) = await h.ApiKeyService.CreateUserKeyAsync(h.Owner.Id, "Script", null);

        Assert.Null(await h.ApiKeyService.ValidateAsync("not-a-key"));
        Assert.Null(await h.ApiKeyService.ValidateAsync(ApiKey.TokenPrefix + new string('a', 64)));

        var (expiredKey, expiredToken) = await h.ApiKeyService.CreateUserKeyAsync(
            h.Owner.Id,
            "Expired",
            DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Null(await h.ApiKeyService.ValidateAsync(expiredToken));
        Assert.NotNull(expiredKey.ExpiresAt);

        var (revoked, revokedToken) = await h.ApiKeyService.CreateUserKeyAsync(h.Owner.Id, "Revoked", null);
        await h.ApiKeyService.RevokeAsync(revoked.Id, h.Owner.Id);
        Assert.Null(await h.ApiKeyService.ValidateAsync(revokedToken));

        // The healthy key is untouched by all of that, and revoked keys stay listed.
        Assert.NotNull(await h.ApiKeyService.ValidateAsync(token));
        var listed = await h.ApiKeyService.GetUserKeysAsync(h.Owner.Id);
        Assert.Equal(3, listed.Count);
        Assert.Contains(listed, k => k.RevokedAt is not null);
    }

    [Fact]
    public async Task Validate_DoesNotRewriteLastUsedOnEveryCall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var (key, token) = await h.ApiKeyService.CreateWallKeyAsync(h.WallId, h.Owner.Id, "Sensor", null);

        var first = await h.ApiKeyService.ValidateAsync(token);
        var second = await h.ApiKeyService.ValidateAsync(token);

        Assert.NotNull(first);
        Assert.NotNull(second);

        // A sensor posting every second must not turn every read into a write.
        Assert.Equal(first.LastUsedAt, second.LastUsedAt);
        await using var db = h.CreateContext();
        var row = await db.ApiKeys.AsNoTracking().FirstAsync(k => k.Id == key.Id);
        Assert.Equal(first.LastUsedAt, row.LastUsedAt);
    }

    [Fact]
    public async Task CreateWallKey_RequiresWallAdmin()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var stranger = await h.AddMemberAsync("stranger@test", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.CreateWallKeyAsync(h.WallId, member.Id, "Nope", null));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.GetWallKeysAsync(h.WallId, member.Id, default));

        var (key, _) = await h.ApiKeyService.CreateWallKeyAsync(h.WallId, h.Owner.Id, "Sensor", null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.RevokeAsync(key.Id, stranger.Id));

        var keys = await h.ApiKeyService.GetWallKeysAsync(h.WallId, h.Owner.Id, default);
        Assert.Single(keys);
    }
}
