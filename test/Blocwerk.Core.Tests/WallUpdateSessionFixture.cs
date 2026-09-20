using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Shared plumbing for the resumable-wall-update suites: the three services under test over one
/// <see cref="WallTestHarness"/>, plus the small seeding helpers they all need. Kept in its own file so
/// each suite stays about its own behaviour.
/// </summary>
internal static class WallUpdateSessionFixture
{
    /// <summary>The big-update service, optionally wired to a real change journal.</summary>
    public static WallBigUpdateService BigUpdate(WallTestHarness h, IChangeJournal? journal = null) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance,
            journal);

    public static WallUpdateSessionService Sessions(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, NullLogger<WallUpdateSessionService>.Instance);

    public static WallPanelService Panels(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

    /// <summary>Makes detection return nothing, so staging produces panels and no auto-detected holds.</summary>
    public static void NoDetections(WallTestHarness h)
    {
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold>()));
    }

    /// <summary>A centre-only capture — the smallest staged set the center-first rule accepts.</summary>
    public static List<BigUpdatePhoto> CentrePhoto() =>
        [new BigUpdatePhoto([1, 2, 3], "image/jpeg", 0, 0)];

    /// <summary>A centre plus one right-hand neighbour, for the neighbour-decision paths.</summary>
    public static List<BigUpdatePhoto> CentreAndNeighbour() =>
    [
        new BigUpdatePhoto([1, 2, 3], "image/jpeg", 0, 0),
        new BigUpdatePhoto([4, 5, 6], "image/jpeg", 1, 0),
    ];

    /// <summary>Adds a hold on the given staged panel at the staged generation.</summary>
    public static async Task<Guid> AddStagedHoldAsync(
        WallTestHarness h, Guid panelId, int stagedGen, double x = 0.5, double y = 0.5)
    {
        await using var db = h.CreateContext();
        var hold = new Hold
        {
            WallId = h.WallId,
            WallPanelId = panelId,
            X = x,
            Y = y,
            Radius = 0.02,
            Generation = stagedGen,
            IsAutoDetected = true,
            NeedsReview = true,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    /// <summary>The staged panel at a grid position of the in-flight update.</summary>
    public static async Task<Guid> PanelIdAsync(WallTestHarness h, int col, int row)
    {
        await using var db = h.CreateContext();
        var panel = await db.WallPanels.SingleAsync(p =>
            p.WallId == h.WallId && p.Col == col && p.Row == row && p.StagedPhoto != null);
        return panel.Id;
    }
}
