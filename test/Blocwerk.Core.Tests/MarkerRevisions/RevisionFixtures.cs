// <copyright file="RevisionFixtures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.MarkerPlanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// Revision 1 (built from the measured wall) and revision 2 (filler 4 shrunk from 125 to 60 mm under its id,
/// 5 moved 80 mm, 44 added), and head-on "photos" of either: every marker of segment 0 projected at
/// <see cref="Scale"/> px/mm, so apparent sizes and distances are what the chosen revision plans.
/// </summary>
internal static class RevisionFixtures
{
    public const double Scale = 0.18;

    public static MarkerPlan Rev1 { get; } =
        MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(RegistrationFixtures.Rev1Json), AtticMarkerPlan.Photo);

    public static MarkerPlan Rev2 { get; } = Rev1 with
    {
        Markers = Rev1.Markers
            .Select(m => m.Id == 4 ? m with { SizeMm = 60 } : m.Id == 5 ? m with { YMm = m.YMm + 80 } : m)
            .Append(Rev1.Markers.Single(m => m.Id == 0) with { Id = 44, XMm = 1500, YMm = 1500, Role = MarkerRole.Filler })
            .ToList(),
    };

    public static IReadOnlyList<RevisionCandidate> Candidates(DateTimeOffset? rev1From = null, DateTimeOffset? rev2From = null) =>
        [new RevisionCandidate(1, Rev1, rev1From), new RevisionCandidate(2, Rev2, rev2From)];

    /// <summary>The markers <paramref name="ids"/> as <paramref name="shown"/> places and sizes them.</summary>
    public static List<DetectedMarker> Photo(MarkerPlan shown, params int[] ids) =>
        ids.Select(id => shown.Markers.Single(m => m.Id == id)).Select(Project).ToList();

    public static List<ObservedMarker> Observed(MarkerPlan shown, params int[] ids) =>
        Photo(shown, ids).Select(ObservedMarker.From).ToList();

    /// <summary>A wall with a legacy model, plan revision 1 with its (active) model, and revision 2 saved on top.</summary>
    public static async Task<MarkerPlanService> SeedAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        var glyphs = WallGlyphSettingsTests.Service(h);
        Assert.True((await glyphs.ImportGeometryAsync(h.WallId, RegistrationFixtures.Rev1Json, "legacy")).Succeeded);
        var plans = new MarkerPlanService(h.DbContextFactory, h.CurrentUser, NullLogger<MarkerPlanService>.Instance);
        Assert.True((await plans.SavePlanAsync(h.WallId, Rev1)).Saved);
        var model = await glyphs.ImportGeometryAsync(h.WallId, RegistrationFixtures.Rev1Json, "rev1", "capture", new GeometryImportOptions(1));
        Assert.True(model.Succeeded);
        Assert.Equal(2, (await plans.SavePlanAsync(h.WallId, Rev2)).Revision);
        return plans;
    }

    private static DetectedMarker Project(PlanMarker m)
    {
        var side = m.SizeMm * Scale;
        var cx = 50 + (m.XMm * Scale);
        var cy = 950 - (m.YMm * Scale);
        return EnrichmentFakes.Square(m.Id, cx - (side / 2), cy - (side / 2), side);
    }
}
