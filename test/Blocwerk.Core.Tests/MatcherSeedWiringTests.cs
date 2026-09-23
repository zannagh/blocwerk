using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The marker seed reaches the matcher ONLY on glyph walls. A wall without <see cref="Wall.GlyphsEnabled"/>
/// must behave exactly as before — every matcher call gets <c>seed == null</c> (hence no anchors) — even
/// when marker observations happen to exist for its panels.
/// </summary>
public class MatcherSeedWiringTests
{
    [Fact]
    public async Task NonMarkerWall_EveryMatcherCallGetsNoSeed()
    {
        using var h = new WallTestHarness();
        var wallId = await SeedAsync(h, glyphs: false);
        var matcher = new CapturingMatcher();

        await Service(h, matcher).ResumeAsync(wallId);

        // Centre carryover, the neighbour's own carryover, and the centre↔neighbour overlap.
        Assert.Equal(3, matcher.Seeds.Count);
        Assert.All(matcher.Seeds, s => Assert.Null(s));
    }

    [Fact]
    public async Task MarkerWall_SeedsTheCarryoverAndTheOverlapFromSharedMarkers()
    {
        using var h = new WallTestHarness();
        var wallId = await SeedAsync(h, glyphs: true);
        var matcher = new CapturingMatcher();

        await Service(h, matcher).ResumeAsync(wallId);

        Assert.Equal(3, matcher.Seeds.Count);
        Assert.All(matcher.Seeds, s =>
        {
            Assert.NotNull(s);
            Assert.Equal(HoldOverlapSeedSource.SharedMarkers, s!.Source);
            Assert.Equal(2, s.MarkerCount);
            Assert.Empty(s.Anchors);
        });
    }

    [Fact]
    public async Task Loader_ReturnsNull_ForANonMarkerWall()
    {
        using var h = new WallTestHarness();
        var wallId = await SeedAsync(h, glyphs: false);
        await using var db = h.CreateContext();
        var wall = await db.Walls.FindAsync(wallId);
        var panels = db.WallPanels.Where(p => p.WallId == wallId).ToList();
        var side = new OverlapSeedSide(panels[0].Id, false, Png(10), []);

        Assert.Null(await OverlapSeedLoader.LoadAsync(db, wall!, side, side, NullLogger.Instance));
    }

    private static WallBigUpdateService Service(WallTestHarness h, IHoldOverlapMatcher matcher) =>
        new(h.DbContextFactory, h.CurrentUser, h.HoldDetection, matcher, NullLogger<WallBigUpdateService>.Instance);

    /// <summary>A live gen-2 wall (centre + right neighbour) with a staged gen-3 update of both, each photo showing markers 0 and 1.</summary>
    private static async Task<Guid> SeedAsync(WallTestHarness h, bool glyphs)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);
        var centrePhoto = Png(0);
        var wall = new Wall
        {
            Name = "Seeded", OwnerId = h.Owner.Id, CurrentGeneration = 2, GlyphsEnabled = glyphs,
            Photo = centrePhoto, PhotoContentType = "image/png", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var liveCentre = Panel(wall.Id, 0, 2, centrePhoto, null);
        var liveRight = Panel(wall.Id, 1, 2, Png(1), null);
        var stagedCentre = Panel(wall.Id, 0, 3, null, Png(2));
        var stagedRight = Panel(wall.Id, 1, 3, null, Png(3));
        db.WallPanels.AddRange(liveCentre, liveRight, stagedCentre, stagedRight);

        for (var i = 0; i < 33; i++)
        {
            db.Holds.Add(new Hold { WallId = wall.Id, WallPanelId = liveCentre.Id, X = 0.1 + (0.02 * i), Y = 0.2 + (0.015 * i), Radius = 0.02, Generation = 2 });
        }

        db.Holds.Add(new Hold { WallId = wall.Id, WallPanelId = liveRight.Id, X = 0.5, Y = 0.5, Radius = 0.02, Generation = 2 });
        db.Holds.Add(new Hold { WallId = wall.Id, WallPanelId = stagedCentre.Id, X = 0.11, Y = 0.21, Radius = 0.02, Generation = 3, IsAutoDetected = true });
        db.Holds.Add(new Hold { WallId = wall.Id, WallPanelId = stagedRight.Id, X = 0.51, Y = 0.5, Radius = 0.02, Generation = 3, IsAutoDetected = true });

        foreach (var (panel, staged, shift) in new[] { (liveCentre, false, 0.0), (liveRight, false, 0.05), (stagedCentre, true, 0.02), (stagedRight, true, 0.07) })
        {
            db.WallMarkerObservations.Add(Observation(panel, staged, 0, 0.2 + shift, 0.3));
            db.WallMarkerObservations.Add(Observation(panel, staged, 1, 0.6 + shift, 0.5));
        }

        await db.SaveChangesAsync();
        return wall.Id;
    }

    private static WallPanel Panel(Guid wallId, int col, int gen, byte[]? photo, byte[]? staged) => new()
    {
        WallId = wallId, Col = col, Row = 0, Generation = gen, Photo = photo, PhotoContentType = photo is null ? null : "image/png",
        StagedPhoto = staged, StagedPhotoContentType = staged is null ? null : "image/png",
    };

    private static WallMarkerObservation Observation(WallPanel panel, bool staged, int id, double x, double y)
    {
        const double s = 0.05;
        double[][] corners = [[x, y], [x + s, y], [x + s, y + s], [x, y + s]];
        return new WallMarkerObservation
        {
            WallPanelId = panel.Id, PanelGeneration = panel.Generation, FromStagedPhoto = staged, MarkerId = id,
            CornersJson = JsonSerializer.Serialize(corners), SidePx = 20,
        };
    }

    /// <summary>A small decodable PNG, distinct per <paramref name="tag"/>.</summary>
    private static byte[] Png(int tag)
    {
        using var bitmap = new SKBitmap(400, 300);
        bitmap.Erase(new SKColor((byte)(40 * tag), 90, 120));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Records every seed it is handed; proposes nothing.</summary>
    private sealed class CapturingMatcher : IHoldOverlapMatcher
    {
        public List<HoldOverlapSeed?> Seeds { get; } = [];

        public HoldOverlapResult Match(
            byte[] leftImage,
            IReadOnlyList<MatcherHold> leftHolds,
            byte[] rightImage,
            IReadOnlyList<MatcherHold> rightHolds,
            HoldOverlapDirection direction,
            ILogger? diag = null,
            HoldOverlapSeed? seed = null)
        {
            Seeds.Add(seed);
            return new HoldOverlapResult([], leftHolds.Select(x => x.Id).ToList(), rightHolds.Select(x => x.Id).ToList());
        }
    }
}
