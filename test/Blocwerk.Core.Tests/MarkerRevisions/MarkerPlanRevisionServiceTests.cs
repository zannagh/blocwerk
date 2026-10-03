// <copyright file="MarkerPlanRevisionServiceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.Enums;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Tests.MarkerPlanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>Plan revisions through the service: numbering, history, "changes since the last capture", PDF.</summary>
public class MarkerPlanRevisionServiceTests
{
    [Fact]
    public async Task Saves_AreNumbered_AndSavingTheSamePlanAgainAddsNoRevision()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var service = Service(h);
        var shrunk = AtticMarkerPlan.Plan with
        {
            Markers = AtticMarkerPlan.Plan.Markers.Select(m => m.Role == MarkerRole.Filler ? m with { SizeMm = 60 } : m).ToList(),
        };

        var first = await service.SavePlanAsync(h.WallId, AtticMarkerPlan.Plan);
        var same = await service.SavePlanAsync(h.WallId, AtticMarkerPlan.Plan);
        var second = await service.SavePlanAsync(h.WallId, shrunk);

        Assert.Equal((1, 1, 2), (first.Revision!.Value, same.Revision!.Value, second.Revision!.Value));
        Assert.True(same.Unchanged);
        var history = await service.GetRevisionsAsync(h.WallId);
        Assert.Equal([2, 1], history.Select(r => r.Revision));
        Assert.True(history[0].IsCurrent);
        Assert.Equal(AtticMarkerPlan.Plan.Markers, (await service.GetRevisionAsync(h.WallId, 1))!.Markers);
    }

    [Fact]
    public async Task ChangesSinceLastCapture_ComparesWithTheActiveModelsMarkers()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var service = Service(h);
        Assert.Null(await service.GetChangesSinceLastCaptureAsync(h.WallId, AtticMarkerPlan.Plan));
        Assert.True((await WallGlyphSettingsTests.Service(h).ImportGeometryAsync(h.WallId, RegistrationFixtures.Rev1Json, "legacy")).Succeeded);
        var rev1 = (await service.BuildFromMeasuredGeometryAsync(h.WallId, AtticMarkerPlan.Photo))!;
        await service.SavePlanAsync(h.WallId, rev1);

        var none = await service.GetChangesSinceLastCaptureAsync(h.WallId);
        var edited = await service.GetChangesSinceLastCaptureAsync(h.WallId, RealCaptureRevisionTests.RevisionTwo(rev1));

        Assert.True(none!.Diff.IsEmpty);
        Assert.Null(none.BaselineRevision);
        Assert.Contains("before any plan", none.BaselineLabel);
        Assert.Equal([44, 45, 46, 47], edited!.Diff.Added);
        Assert.Equal([24, 25, 26, 27], edited.Diff.Removed);
        Assert.Equal([4, 5], edited.Diff.Resized);
    }

    [Fact]
    public async Task History_IsForAdminsOnly()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("climber@test", WallRole.Member);

        await Assert.ThrowsAnyAsync<Exception>(() => Service(h).GetRevisionsAsync(h.WallId));
    }

    [Fact]
    public void Pdf_OnlyChangedMarkers_PrintsJustThoseMarkerPages()
    {
        var all = MarkerPlanPdf.Render(AtticMarkerPlan.Plan, "The Attic");
        var only = MarkerPlanPdf.Render(AtticMarkerPlan.Plan, "The Attic", new HashSet<int> { 4, 5 });

        Assert.Equal(2 + AtticMarkerPlan.Plan.Markers.Count, Pages(all));
        Assert.Equal(2 + 2, Pages(only));
    }

    private static int Pages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        return text.Split("/MediaBox [0 0 595.2").Length - 1;
    }

    private static MarkerPlanService Service(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, NullLogger<MarkerPlanService>.Instance);
}
