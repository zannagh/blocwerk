// <copyright file="HoldColorScenario.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// A wall whose panel photos are <see cref="HoldColorTestPhoto"/>s: every panel shows a blue hold at (0.2, 0.3), a red
/// one at (0.5, 0.3), a green one at (0.8, 0.3) and a yellow one at (0.2, 0.7); the rest is wooden wall.
/// </summary>
internal sealed class HoldColorScenario : IDisposable
{
    public static readonly (double X, double Y) BlueSpot = (0.2, 0.3);
    public static readonly (double X, double Y) RedSpot = (0.5, 0.3);
    public static readonly (double X, double Y) GreenSpot = (0.8, 0.3);
    public static readonly (double X, double Y) YellowSpot = (0.2, 0.7);
    public static readonly (double X, double Y) WallSpot = (0.6, 0.75);

    private HoldColorScenario(WallTestHarness harness)
    {
        Harness = harness;
    }

    public WallTestHarness Harness { get; }

    public Guid WallId => Harness.WallId;

    public static async Task<HoldColorScenario> CreateAsync(int oldHolds = 0)
    {
        var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: oldHolds);
        return new HoldColorScenario(harness);
    }

    /// <summary>The panel photo, optionally with other colours on the spots.</summary>
    public static byte[] Photo(SKColor? blueSpot = null, SKColor? greenSpot = null) => HoldColorTestPhoto.Build(
        (BlueSpot.X, BlueSpot.Y, 0.06, blueSpot ?? HoldColorTestPhoto.Blue),
        (RedSpot.X, RedSpot.Y, 0.06, HoldColorTestPhoto.Red),
        (GreenSpot.X, GreenSpot.Y, 0.06, greenSpot ?? HoldColorTestPhoto.Green),
        (YellowSpot.X, YellowSpot.Y, 0.06, HoldColorTestPhoto.Yellow));

    /// <summary>A panel: live with its photo, or staged for the update to <paramref name="generation"/>.</summary>
    public async Task<Guid> AddPanelAsync(int col, int generation = 0, bool staged = false, byte[]? photo = null, Guid? wallId = null)
    {
        await using var db = Harness.CreateContext();
        var panel = new WallPanel { WallId = wallId ?? WallId, Col = col, Row = 0, Generation = generation };
        if (staged)
        {
            panel.StagedPhoto = photo ?? Photo();
            panel.StagedPhotoContentType = "image/png";
        }
        else
        {
            panel.Photo = photo ?? Photo();
            panel.PhotoContentType = "image/png";
        }

        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }

    /// <summary>A detected hold on a spot of a panel.</summary>
    public async Task<Guid> AddHoldAsync(
        Guid panelId, (double X, double Y) spot, int generation = 0, Action<Hold>? configure = null, Guid? wallId = null)
    {
        await using var db = Harness.CreateContext();
        var hold = new Hold
        {
            WallId = wallId ?? WallId, WallPanelId = panelId, X = spot.X, Y = spot.Y, Radius = 0.07, Generation = generation, IsAutoDetected = true,
        };
        configure?.Invoke(hold);
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    public async Task LinkAsync(Guid a, Guid b)
    {
        await using var db = Harness.CreateContext();
        db.HoldLinks.Add(new HoldLink { WallId = WallId, HoldAId = a, HoldBId = b });
        await db.SaveChangesAsync();
    }

    public async Task<Hold> GetAsync(Guid id)
    {
        await using var db = Harness.CreateContext();
        return await db.Holds.AsNoTracking().FirstAsync(h => h.Id == id);
    }

    public async Task SetAsync(Guid id, Action<Hold> change)
    {
        await using var db = Harness.CreateContext();
        change(await db.Holds.FirstAsync(h => h.Id == id));
        await db.SaveChangesAsync();
    }

    public HoldColorProposalService Service() =>
        new(Harness.DbContextFactory, Harness.CurrentUser, new HoldColorProposer(), NullLogger<HoldColorProposalService>.Instance);

    public WallBigUpdateService BigUpdate(IHoldColorProposer? proposer = null) =>
        new(
            Harness.DbContextFactory, Harness.CurrentUser, Harness.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance, colorProposer: proposer);

    public void Dispose() => Harness.Dispose();
}
