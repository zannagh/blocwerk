using System.Security.Claims;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A session must die with the account behind it. Deleting on one device leaves every other tab
/// holding a cookie that is still cryptographically valid for hours, and a stolen cookie lives just
/// as long — so resolution, not the login form, is where a tombstone has to be refused.
/// </summary>
public class DeletedAccountSessionTests
{
    private const string Subject = "leaver-subject";

    [Fact]
    public async Task AUidSessionCannotResolveATombstoneAndRePersonaliseIt()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var leaver = await SeedTombstoneAsync(harness);

        // Exactly what a password session's cookie carries, still perfectly valid.
        var service = ServiceFor(harness, Principal(uid: leaver, authTime: null));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCurrentUserAsync());

        // And nothing was written back onto the erased row on the way out.
        await using var db = harness.CreateContext();
        var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == leaver);
        Assert.Equal(PlaceholderIdentity.DisplayName, row.DisplayName);
        Assert.True(row.IsDeleted);
    }

    [Fact]
    public async Task AStaleOAuthCookieForADeletedAccountDoesNotSilentlyCreateANewOne()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var leaver = await SeedTombstoneAsync(harness);
        int before = await CountUsersAsync(harness);

        // Erasure dropped the identity rows and rewrote the identifier, so an old cookie for this
        // account matches nothing at all — and used to land in the "no user yet, make one" branch.
        var service = ServiceFor(harness, Principal(uid: null, authTime: null));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCurrentUserAsync());

        Assert.Equal(before, await CountUsersAsync(harness));

        await using var db = harness.CreateContext();
        Assert.Empty(await db.Users.Where(u => u.Id != leaver && u.Identifier.Contains(Subject)).ToListAsync());
    }

    [Fact]
    public async Task ASignInThatJustHappenedStillCreatesTheAccount()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        // The same claims, on a cookie minted seconds ago by the OAuth callback. Signup must still
        // work — the gate is on staleness, not on creation.
        var service = ServiceFor(harness, Principal(uid: null, authTime: DateTimeOffset.UtcNow));

        var created = await service.GetCurrentUserAsync();

        Assert.Equal($"New Climber__{Subject}", created.Identifier);
    }

    [Fact]
    public async Task NoSessionMayEverResolveTheGhostRow()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var service = ServiceFor(harness, Principal(uid: GhostUser.Id, authTime: DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCurrentUserAsync());
    }

    /// <summary>
    /// The account-merge case, and the one the uid branch exists for. A merge moves the user's
    /// history onto the surviving account and DROPS the absorbed row, so every other tab is left
    /// holding a password cookie whose uid names a row that is simply not there any more.
    /// </summary>
    /// <remarks>
    /// The branch has to be terminal, not merely "did not match". Falling through put these claims
    /// into identifier resolution and then into creation — silently minting a blank third account and
    /// forking the history the merge had just finished joining up. The claims here are deliberately
    /// FRESH, because creation is the outcome being ruled out: a stale cookie would be refused by the
    /// auth_time gate no matter what this branch did, and would prove nothing about it.
    /// </remarks>
    [Fact]
    public async Task AUidNamingAMissingRowIsRefusedRatherThanTurnedIntoANewAccount()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        int before = await CountUsersAsync(harness);

        var service = ServiceFor(harness, Principal(uid: Guid.NewGuid(), authTime: DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCurrentUserAsync());

        Assert.Equal(before, await CountUsersAsync(harness));

        await using var db = harness.CreateContext();
        Assert.Empty(await db.Users.Where(u => u.Identifier.Contains(Subject)).ToListAsync());
    }

    /// <summary>
    /// A forged or corrupted auth_time must read as "not fresh", never as an exception out of the
    /// resolution path. long.TryParse accepts numbers no calendar can hold, and the range check is
    /// what keeps <see cref="DateTimeOffset.FromUnixTimeSeconds"/> from throwing straight into a 500.
    /// </summary>
    [Fact]
    public async Task AnOutOfRangeAuthTimeIsNotFreshRatherThanAnError()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        int before = await CountUsersAsync(harness);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Subject),
            new(ClaimTypes.Name, "New Climber"),
            new("provider", "github"),
            new(AuthFreshness.ClaimType, "9999999999999"),
        };

        var service = ServiceFor(harness, new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCurrentUserAsync());

        Assert.Equal(before, await CountUsersAsync(harness));
    }

    private static async Task<Guid> SeedTombstoneAsync(WallTestHarness harness)
    {
        var leaver = new User
        {
            Identifier = $"Leaver__{Subject}",
            DisplayName = "Leaver",
        };

        await using (var db = harness.CreateContext())
        {
            db.Users.Add(leaver);
            db.UserIdentities.Add(new UserIdentity
            {
                UserId = leaver.Id,
                Provider = "github",
                ProviderUserId = Subject,
            });
            await db.SaveChangesAsync();
        }

        var deletion = DeletionFixture.CreateService(harness, Substitute.For<IBetaVideoStorage>());
        harness.ActingUser = leaver;
        Assert.True(await deletion.DeleteAsync(leaver.Id));
        harness.ActingUser = harness.Owner;

        return leaver.Id;
    }

    private static async Task<int> CountUsersAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        return await db.Users.CountAsync();
    }

    /// <summary>
    /// The claims a still-valid session presents. <paramref name="uid"/> is the password/TOTP path;
    /// without it the provider claims drive the OAuth path.
    /// </summary>
    private static ClaimsPrincipal Principal(Guid? uid, DateTimeOffset? authTime)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Subject),
            new(ClaimTypes.Name, uid is null ? "New Climber" : "Leaver"),
            new("provider", "github"),
        };

        if (uid is { } id)
        {
            claims.Add(new Claim("uid", id.ToString()));
        }

        if (authTime is { } at)
        {
            claims.Add(new Claim(AuthFreshness.ClaimType, at.ToUnixTimeSeconds().ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    private static CurrentUserService ServiceFor(WallTestHarness harness, ClaimsPrincipal principal)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = principal });

        return new CurrentUserService(
            new BlocwerkSettings(),
            harness.DbContextFactory,
            Substitute.For<IPasswordLoginService>(),
            Substitute.For<ITotpService>(),
            authenticationStateProvider: null,
            accessor: accessor);
    }
}
