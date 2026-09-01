using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers kiosk mode's data foundation: a tablet gets its own key scope rather than a wall key's
/// write access, consent is per member row and revocable, and the optional PIN never leaves the
/// hasher.
/// </summary>
public class KioskTests
{
    [Fact]
    public async Task CreateKioskKey_IsKioskScoped_AndCarriesTheWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var (key, token) = await h.ApiKeyService.CreateKioskKeyAsync(h.WallId, h.Owner.Id, "Tablet", null);

        Assert.StartsWith(ApiKey.TokenPrefix, token);
        Assert.Equal(ApiKeyScope.Kiosk, key.Scope);
        Assert.Equal(h.WallId, key.WallId);

        // Validation reports the scope and wall, which is what a later kiosk request needs.
        var validated = await h.ApiKeyService.ValidateAsync(token);
        Assert.NotNull(validated);
        Assert.Equal(ApiKeyScope.Kiosk, validated.Scope);
        Assert.Equal(h.WallId, validated.WallId);
        Assert.Equal(h.WallId, await h.ApiKeyService.ValidateKioskAsync(token));

        // A wall key is not a kiosk key: the tablet scope must not be reachable by the sensor scope.
        var (_, wallToken) = await h.ApiKeyService.CreateWallKeyAsync(h.WallId, h.Owner.Id, "Sensor", null);
        Assert.Null(await h.ApiKeyService.ValidateKioskAsync(wallToken));
    }

    [Fact]
    public async Task CreateKioskKey_RequiresWallAdmin()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var member = await h.AddMemberAsync("member@test", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.CreateKioskKeyAsync(h.WallId, member.Id, "Nope", null));
    }

    [Fact]
    public async Task KioskKey_IsListedWithTheWallsKeys_AndStopsValidatingOnceRevoked()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        await h.ApiKeyService.CreateWallKeyAsync(h.WallId, h.Owner.Id, "Sensor", null);
        var (kiosk, kioskToken) = await h.ApiKeyService.CreateKioskKeyAsync(h.WallId, h.Owner.Id, "Tablet", null);

        // The wall's key panel must be able to see (and therefore revoke) the tablet's key.
        var listed = await h.ApiKeyService.GetWallKeysAsync(h.WallId, h.Owner.Id, default);
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, k => k.Scope == ApiKeyScope.Kiosk && k.Id == kiosk.Id);

        await h.ApiKeyService.RevokeAsync(kiosk.Id, h.Owner.Id);
        Assert.Null(await h.ApiKeyService.ValidateAsync(kioskToken));
        Assert.Null(await h.ApiKeyService.ValidateKioskAsync(kioskToken));
    }

    [Fact]
    public async Task Consent_SetsTimestamp_AndRevokeClearsTimestampAndPin()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        Assert.False(await h.KioskService.HasConsentedAsync(h.WallId));

        await h.KioskService.ConsentAsync(h.WallId, "1234");
        Assert.True(await h.KioskService.HasConsentedAsync(h.WallId));

        await using (var db = h.CreateContext())
        {
            var row = await ReadMemberAsync(db, h.WallId, h.Owner.Id);
            Assert.NotNull(row.KioskConsentedAt);
            Assert.NotNull(row.KioskPinHash);

            // The PIN is only ever stored as a hash.
            Assert.DoesNotContain("1234", row.KioskPinHash);
        }

        await h.KioskService.RevokeConsentAsync(h.WallId);
        Assert.False(await h.KioskService.HasConsentedAsync(h.WallId));

        await using (var db = h.CreateContext())
        {
            var row = await ReadMemberAsync(db, h.WallId, h.Owner.Id);
            Assert.Null(row.KioskConsentedAt);
            Assert.Null(row.KioskPinHash);
        }
    }

    [Fact]
    public async Task Consent_RejectsAMalformedPin_AndChangesNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        foreach (var bad in new[] { "123", "123456789", "12a4", " 12 " })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.KioskService.ConsentAsync(h.WallId, bad));
        }

        Assert.False(await h.KioskService.HasConsentedAsync(h.WallId));

        // Whitespace-only reads as "no PIN", which is a legitimate consent rather than a rejection.
        await h.KioskService.ConsentAsync(h.WallId, "   ");
        await using var db = h.CreateContext();
        var row = await ReadMemberAsync(db, h.WallId, h.Owner.Id);
        Assert.NotNull(row.KioskConsentedAt);
        Assert.Null(row.KioskPinHash);
    }

    [Fact]
    public async Task Consent_RequiresMembershipOfThatWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWallId = await SeedSecondWallAsync(h);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.KioskService.ConsentAsync(otherWallId, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.KioskService.ConsentAsync(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task GetConsentingUsers_ReturnsOnlyConsentingMembersOfThatWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var withPin = await h.AddMemberAsync("pin@test", WallRole.Member);
        var withoutPin = await h.AddMemberAsync("nopin@test", WallRole.Member);
        await h.AddMemberAsync("silent@test", WallRole.Member);

        var otherWallId = await SeedSecondWallAsync(h);
        var elsewhere = await AddMemberToAsync(h, otherWallId, "elsewhere@test");

        h.ActingUser = withPin;
        await h.KioskService.ConsentAsync(h.WallId, "4321");
        h.ActingUser = withoutPin;
        await h.KioskService.ConsentAsync(h.WallId, null);
        h.ActingUser = elsewhere;
        await h.KioskService.ConsentAsync(otherWallId, null);

        var picker = await h.KioskService.GetConsentingUsersAsync(h.WallId);

        Assert.Equal(2, picker.Count);
        Assert.DoesNotContain(picker, u => u.UserId == elsewhere.Id);
        Assert.True(picker.Single(u => u.UserId == withPin.Id).RequiresPin);
        Assert.False(picker.Single(u => u.UserId == withoutPin.Id).RequiresPin);
        Assert.All(picker, u => Assert.False(u.HasAvatar));

        // The other wall's picker is independent and shows only its own consenting member.
        var otherPicker = await h.KioskService.GetConsentingUsersAsync(otherWallId);
        Assert.Equal(elsewhere.Id, Assert.Single(otherPicker).UserId);
    }

    [Fact]
    public async Task VerifyPin_AcceptsTheRightPin_AndRefusesEverythingElse()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var pinned = await h.AddMemberAsync("pin@test", WallRole.Member);
        var open = await h.AddMemberAsync("nopin@test", WallRole.Member);
        var silent = await h.AddMemberAsync("silent@test", WallRole.Member);

        h.ActingUser = pinned;
        await h.KioskService.ConsentAsync(h.WallId, "4321");
        h.ActingUser = open;
        await h.KioskService.ConsentAsync(h.WallId, null);

        Assert.True(await h.KioskService.VerifyPinAsync(h.WallId, pinned.Id, "4321"));
        Assert.False(await h.KioskService.VerifyPinAsync(h.WallId, pinned.Id, "1234"));
        Assert.False(await h.KioskService.VerifyPinAsync(h.WallId, pinned.Id, null));
        Assert.False(await h.KioskService.VerifyPinAsync(h.WallId, pinned.Id, string.Empty));

        Assert.True(await h.KioskService.VerifyPinAsync(h.WallId, open.Id, null));
        Assert.True(await h.KioskService.VerifyPinAsync(h.WallId, open.Id, string.Empty));

        // Never consented, and never even a member: both are simply refused.
        Assert.False(await h.KioskService.VerifyPinAsync(h.WallId, silent.Id, null));
        Assert.False(await h.KioskService.VerifyPinAsync(h.WallId, silent.Id, "4321"));
        Assert.False(await h.KioskService.VerifyPinAsync(h.WallId, Guid.NewGuid(), null));

        // Revoking consent immediately closes the pick, PIN or no PIN.
        h.ActingUser = pinned;
        await h.KioskService.RevokeConsentAsync(h.WallId);
        Assert.False(await h.KioskService.VerifyPinAsync(h.WallId, pinned.Id, "4321"));
    }

    private static Task<WallMember> ReadMemberAsync(Blocwerk.Core.Data.BlocwerkDbContext db, Guid wallId, Guid userId)
    {
        return db.WallMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(m => m.WallId == wallId && m.UserId == userId);
    }

    /// <summary>Seeds a second wall that <see cref="WallTestHarness.Owner"/> is not a member of.</summary>
    private static async Task<Guid> SeedSecondWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();

        var stranger = new User { Identifier = "other-owner@test", DisplayName = "Other Owner" };
        var wall = new Wall
        {
            Name = "Other Wall",
            OwnerId = stranger.Id,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
        };

        db.Users.Add(stranger);
        db.Walls.Add(wall);
        await db.SaveChangesAsync();
        return wall.Id;
    }

    private static async Task<User> AddMemberToAsync(WallTestHarness h, Guid wallId, string identifier)
    {
        await using var db = h.CreateContext();

        var user = new User { Identifier = identifier, DisplayName = identifier };
        db.Users.Add(user);
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = user.Id, Role = WallRole.Member });
        await db.SaveChangesAsync();
        return user;
    }
}
