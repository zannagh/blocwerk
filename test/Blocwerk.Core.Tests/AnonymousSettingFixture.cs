using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A seeded wall with a real kiosk API key, an anonymous current-user service, and a
/// <see cref="BoulderService"/> wired the way production wires it for a tablet.
/// </summary>
/// <remarks>
/// Its own file rather than nested in one test class, because two test files exercise the same gate:
/// <see cref="KioskAnonymousSettingTests"/> for the wall-level conditions and
/// <see cref="KioskAnonymousKeySettingTests"/> for the per-key one.
/// </remarks>
internal sealed class AnonymousSettingFixture : IDisposable
{
    public WallTestHarness Harness { get; private set; } = null!;

    public ApiKey KioskKey { get; private set; } = null!;

    public Guid FirstHoldId { get; private set; }

    public static async Task<AnonymousSettingFixture> CreateAsync()
    {
        var fixture = new AnonymousSettingFixture { Harness = new WallTestHarness() };
        var holds = await fixture.Harness.SeedWallAsync();
        fixture.FirstHoldId = holds[0].Id;

        var (key, _) = await fixture.Harness.ApiKeyService.CreateKioskKeyAsync(
            fixture.Harness.WallId, fixture.Harness.Owner.Id, "Tablet", null);
        fixture.KioskKey = key;

        return fixture;
    }

    /// <summary>An <see cref="IKioskContext"/> saying exactly what the caller asks it to say.</summary>
    public static IKioskContext KioskContextFor(bool isKiosk, Guid? wallId, Guid? keyId)
    {
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(isKiosk);
        kiosk.KioskWallId.Returns(wallId);
        kiosk.KioskApiKeyId.Returns(keyId);
        return kiosk;
    }

    /// <summary>The context a correctly registered tablet on the seeded wall resolves to.</summary>
    public IKioskContext LiveKioskContext()
    {
        return KioskContextFor(isKiosk: true, Harness.WallId, KioskKey.Id);
    }

    /// <summary>
    /// A boulder service for an ANONYMOUS session on that tablet: the current-user service
    /// throws, and the context factory stamps the kiosk wall exactly as
    /// <c>KioskScopedDbContextFactory</c> does in production.
    /// </summary>
    public IBoulderService BoulderServiceFor(
        IKioskContext kioskContext,
        bool withKeyValidator = true,
        KioskAnonymousSettingThrottle? throttle = null,
        bool withThrottle = true)
    {
        Harness.CurrentUser.GetCurrentUserAsync()
            .Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));

        return new BoulderService(
            new KioskStampingFactory(Harness.DbContextFactory, kioskContext),
            Harness.CurrentUser,
            Harness.ActivityLog,
            NullLogger<BoulderService>.Instance,
            kioskContext,
            withKeyValidator ? new KioskKeyValidator(Harness.DbContextFactory) : null,
            withThrottle ? throttle ?? new KioskAnonymousSettingThrottle() : null);
    }

    public Task<Boulder> CreateAsync(
        IBoulderService boulders,
        Guid? wallId = null,
        string name = "Kiosk Problem",
        IReadOnlyList<Guid>? setterUserIds = null)
    {
        return boulders.CreateBoulderAsync(
            wallId ?? Harness.WallId,
            name,
            null,
            [new BoulderHoldInput(FirstHoldId)],
            setterUserIds: setterUserIds);
    }

    /// <summary>Sets the TABLET's own flag directly, bypassing the service and therefore its authz.</summary>
    public async Task SetKeyAnonymousSettingAsync(bool allowed)
    {
        await using var db = Harness.CreateContext();
        var key = await db.ApiKeys.IgnoreQueryFilters().FirstAsync(k => k.Id == KioskKey.Id);
        key.AllowAnonymousKioskSetting = allowed;
        await db.SaveChangesAsync();
    }

    /// <summary>Flips the wall's opt-in on, directly, so the test is not also testing the UI.</summary>
    public async Task AllowAnonymousSettingAsync()
    {
        await using var db = Harness.CreateContext();
        var wall = await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == Harness.WallId);
        wall.AllowAnonymousKioskSetting = true;
        await db.SaveChangesAsync();
    }

    public async Task<List<Boulder>> StoredBouldersAsync(Guid? wallId = null)
    {
        await using var db = Harness.CreateContext();
        return await db.Boulders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(b => b.Setters)
            .Where(b => b.WallId == (wallId ?? Harness.WallId))
            .ToListAsync();
    }

    public async Task<Guid> SeedSecondWallAsync(bool allowAnonymousSetting)
    {
        await using var db = Harness.CreateContext();
        var wall = new Wall
        {
            Name = "Other Wall",
            OwnerId = Harness.Owner.Id,
            Photo = [1],
            PhotoContentType = "image/jpeg",
            AllowAnonymousKioskSetting = allowAnonymousSetting,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember
        {
            WallId = wall.Id,
            UserId = Harness.Owner.Id,
            Role = WallRole.Admin,
        });
        db.Holds.Add(new Hold { WallId = wall.Id, X = 0.5, Y = 0.5, Radius = 0.02 });
        await db.SaveChangesAsync();
        return wall.Id;
    }

    /// <summary>Adds a member of <paramref name="wallId"/> who consents to that wall's kiosk.</summary>
    public async Task<User> AddConsentingMemberAsync(string identifier, Guid? wallId = null)
    {
        var targetWallId = wallId ?? Harness.WallId;
        User user;

        if (wallId is null)
        {
            user = await Harness.AddMemberAsync(identifier, WallRole.Member);
        }
        else
        {
            await using var db = Harness.CreateContext();
            user = new User { Identifier = identifier, DisplayName = identifier };
            db.Users.Add(user);
            db.WallMembers.Add(new WallMember
            {
                WallId = targetWallId,
                UserId = user.Id,
                Role = WallRole.Member,
            });
            await db.SaveChangesAsync();
        }

        var previous = Harness.ActingUser;
        Harness.ActingUser = user;
        try
        {
            await Harness.KioskService.ConsentAsync(targetWallId, null);
        }
        finally
        {
            Harness.ActingUser = previous;
        }

        return user;
    }

    public void Dispose()
    {
        Harness.Dispose();
    }
}
