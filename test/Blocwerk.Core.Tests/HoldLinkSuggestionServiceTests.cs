// <copyright file="HoldLinkSuggestionServiceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.HoldLinks;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="HoldLinkSuggestionService"/> on a SQLite wall with two live panels and an active one-facet model:
/// storing, the admin gate, linking through the normal path, and remembered rejections.
/// </summary>
public class HoldLinkSuggestionServiceTests
{
    private const string OneFacetJson = """
        {
          "version": 1, "units": "mm", "markerSizeMm": 125.0,
          "segments": [ { "index": 0, "facets": [ { "id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1],
            "extentMm": { "aMin": 0, "aMax": 6000, "bMin": 0, "bMax": 3000 } } ] } ],
          "markers": []
        }
        """;

    [Fact]
    public async Task NearbyPlacedHolds_OnTwoPanels_BecomeOnePendingSuggestion()
    {
        using var h = new WallTestHarness();
        var (a, b, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        var service = NewService(h);

        Assert.Equal(1, await service.RefreshFromPipelineAsync(h.WallId));
        Assert.Equal(1, await service.CountPendingAsync(h.WallId));
        var view = Assert.Single(await service.ListAsync(h.WallId));
        Assert.Equal(HoldLinkPairSuggestion.Key(a, b), (view.A.HoldId, view.B.HoldId));
        Assert.NotEqual(view.A.PanelId, view.B.PanelId);
    }

    [Fact]
    public async Task FarApartHolds_AreNotSuggested()
    {
        using var h = new WallTestHarness();
        await SeedAsync(h, (1000, 1000), (1400, 1000));

        Assert.Equal(0, await NewService(h).RefreshFromPipelineAsync(h.WallId));
    }

    [Fact]
    public async Task SameSpotOnAVolume_IsSuggestedEvenWhenTheFlatPositionsDiffer()
    {
        using var h = new WallTestHarness();
        var (a, b, modelId) = await SeedAsync(h, (4515, 1247), (4378, 1216));
        await PutOnVolumeAsync(h, modelId, (a, 4439, 1323), (b, 4435, 1322));

        var pair = Assert.Single(await NewService(h).ListAsync(h.WallId));

        Assert.True(pair.DistanceMm < 10);
    }

    [Fact]
    public async Task Link_CreatesTheNormalLink_AndTheSuggestionGoes()
    {
        using var h = new WallTestHarness();
        var (a, b, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        var service = NewService(h);
        await service.RefreshFromPipelineAsync(h.WallId);

        await service.LinkAsync(h.WallId, b, a);

        await using var db = h.CreateContext();
        var link = Assert.Single(db.HoldLinks.Where(l => l.WallId == h.WallId));
        Assert.Equal(HoldLinkKind.Same, link.Kind);
        Assert.Equal(0, await service.CountPendingAsync(h.WallId));
        Assert.Empty(db.HoldLinkSuggestions.Where(s => s.WallId == h.WallId));
    }

    [Fact]
    public async Task Reject_IsRemembered_AcrossRefreshes()
    {
        using var h = new WallTestHarness();
        var (a, b, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        var service = NewService(h);
        await service.RefreshFromPipelineAsync(h.WallId);

        await service.RejectAsync(h.WallId, a, b);

        Assert.Equal(0, await service.RefreshFromPipelineAsync(h.WallId));
        Assert.Equal(0, await service.CountPendingAsync(h.WallId));
        await using var db = h.CreateContext();
        Assert.Equal(HoldLinkSuggestionStatus.Rejected, Assert.Single(db.HoldLinkSuggestions).Status);
        Assert.Empty(db.HoldLinks);
    }

    [Fact]
    public async Task WithoutAModel_NothingIsSuggested_AndPendingOnesGo()
    {
        using var h = new WallTestHarness();
        var (_, _, modelId) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        var service = NewService(h);
        await service.RefreshFromPipelineAsync(h.WallId);
        await using (var db = h.CreateContext())
        {
            await db.WallGeometryModels.Where(m => m.Id == modelId).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false));
        }

        Assert.Null(await service.RefreshFromPipelineAsync(h.WallId));
        Assert.Equal(0, await service.CountPendingAsync(h.WallId));
    }

    [Fact]
    public async Task NonAdmins_SeeNothing_AndCannotAnswer()
    {
        using var h = new WallTestHarness();
        var (a, b, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        var service = NewService(h);
        await service.RefreshFromPipelineAsync(h.WallId);
        h.ActingUser = await AddMemberAsync(h);

        Assert.Equal(0, await service.CountPendingAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RejectAsync(h.WallId, a, b));
    }

    [Fact]
    public void FollowUpStep_SaysHowManyWereFound()
    {
        Assert.Equal(string.Empty, SuggestHoldLinksFollowUpStep.Describe(0));
        Assert.Equal("2 holds seem to appear on two photos without a link", SuggestHoldLinksFollowUpStep.Describe(2));
    }

    internal static HoldLinkSuggestionService NewService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            new WallPanelService(h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallPanelService>.Instance),
            NullLogger<HoldLinkSuggestionService>.Instance);

    /// <summary>Two live panels, one placed hold each at the given facet points, and the active model.</summary>
    internal static async Task<(Guid A, Guid B, Guid ModelId)> SeedAsync(WallTestHarness h, (double A, double B) first, (double A, double B) second)
    {
        await h.SeedWallAsync(holdCount: 0);
        await using var db = h.CreateContext();
        var panelA = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 0 };
        var panelB = new WallPanel { WallId = h.WallId, Col = 1, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 0 };
        var holdA = Placed(h.WallId, panelA.Id, first);
        var holdB = Placed(h.WallId, panelB.Id, second);
        var model = new WallGeometryModel { WallId = h.WallId, Json = OneFacetJson, SchemaVersion = 1, Source = "test", IsActive = true };
        db.AddRange(panelA, panelB, holdA, holdB, model);
        await db.SaveChangesAsync();
        return (holdA.Id, holdB.Id, model.Id);
    }

    internal static Hold Placed(Guid wallId, Guid panelId, (double A, double B) at) =>
        new() { WallId = wallId, WallPanelId = panelId, X = 0.5, Y = 0.5, Radius = 0.02, FacetId = "0", PlaneAMm = at.A, PlaneBMm = at.B };

    internal static async Task PutOnVolumeAsync(WallTestHarness h, Guid modelId, params (Guid Hold, double A, double B)[] points)
    {
        await using var db = h.CreateContext();
        var volume = new WallVolume { WallId = h.WallId, GeometryModelId = modelId, FacetId = "0", Index = 1, FootprintJson = "[]", SurfaceJson = "{}" };
        db.WallVolumes.Add(volume);
        foreach (var (id, a, b) in points)
        {
            var hold = await db.Holds.SingleAsync(x => x.Id == id);
            hold.VolumePlacementJson = new HoldVolumePlacement(volume.Id, a, b, 61, [0, 0, 1], hold.PlaneAMm!.Value, hold.PlaneBMm!.Value, "panel").ToJson();
        }

        await db.SaveChangesAsync();
    }

    internal static async Task<User> AddMemberAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = "member@test", DisplayName = "Member" };
        db.Users.Add(user);
        db.WallMembers.Add(new WallMember { WallId = h.WallId, UserId = user.Id, Role = WallRole.Member });
        await db.SaveChangesAsync();
        return user;
    }
}
