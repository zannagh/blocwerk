// <copyright file="CaptureRegistrationTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Tests.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.MarkerRevisions.RegistrationFixtures;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// The Attic's path on prod, end to end through the capture pipeline: a legacy model and no plan; the owner
/// builds revision 1 from the measured wall, then revision 2 (fillers 4/5 shrunk to 60 mm, spares 24–27
/// re-issued as 44–47), then captures. The new model must land in the old model's frame with revision 2
/// recorded — or, when too few markers stayed put, be stored but not activated, with the reason.
/// </summary>
public class CaptureRegistrationTests
{
    private static readonly Geometry.Registration.RigidTransform3D Offset = Transform(-3, 2, [-410, 95, -35]);

    [Fact]
    public async Task ShrunkFillersAndNewIds_CaptureIsRegisteredToTheLegacyModel_AndRecordsRevisionTwo()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.GeometryJson = Solved(Displace(Displace(Move(Rev2Json, Offset, "0", 180), 4, 0, -90, 0.48), 5, 60, 0, 0.48));

        var captureId = await s.StartCaptureAsync(beforeStart: _ => PrepareAsync(h, s, RealCaptureRevisionTests.RevisionTwo));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        var active = await db.WallGeometryModels.SingleAsync(m => m.WallId == h.WallId && m.IsActive);
        Assert.Null(capture.Error);
        Assert.Equal((2, 2), (capture.PlanRevision!.Value, active.PlanRevision!.Value));
        Assert.Equal(capture.GeometryModelId, active.Id);
        var registration = JsonNode.Parse(active.Json)!["quality"]!["registration"]!;
        Assert.Equal([4, 5], registration["changedMarkerIds"]!.AsArray().Select(n => n!.GetValue<int>()));
        var original = WallGeometryDocument.Parse(Rev1Json).FindFacet("0")!.Value.Facet;
        Assert.Equal(original.Origin, WallGeometryDocument.Parse(active.Json).FindFacet("0")!.Value.Facet.Origin);
    }

    [Fact]
    public async Task OnlyTwoMarkersKeptTheirPlace_ModelIsStoredButNotActivated_AndTheAdminIsTold()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.GeometryJson = Solved(Move(Rev1Json, Offset));

        // Revision 2 moves every marker but 0 and 2 by 50 mm: only two ties to the old model are left.
        var captureId = await s.StartCaptureAsync(beforeStart: _ => PrepareAsync(h, s, rev1 => rev1 with
        {
            Markers = rev1.Markers.Select(m => m.Id is 0 or 2 ? m : m with { XMm = m.XMm + 50 }).ToList(),
        }));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        var models = await db.WallGeometryModels.Where(m => m.WallId == h.WallId).ToListAsync();
        Assert.Equal(WallCaptureStatus.StoredNotActivated, capture.Status);
        Assert.Contains("Only 2 marker(s) kept their place (0, 2)", capture.Error);
        Assert.Contains("NOT activated", capture.Error);
        Assert.Equal(2, models.Count);
        Assert.Null(models.Single(m => m.IsActive).PlanRevision);
        Assert.Equal(capture.GeometryModelId, models.Single(m => !m.IsActive).Id);
    }

    [Fact]
    public async Task Draft_CanBePinnedToAStoredRevision_AndAnUnknownOneIsRefused()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        await PrepareAsync(h, s, RealCaptureRevisionTests.RevisionTwo);

        var current = (await s.Service.GetDraftAsync(h.WallId))!.Plan!.Revision;
        var pinned = await s.Service.UsePlanRevisionAsync(draft.CaptureId, 1);
        var unknown = await s.Service.UsePlanRevisionAsync(draft.CaptureId, 9);

        Assert.Equal(2, current);
        Assert.True(pinned.Accepted);
        Assert.Equal(1, (await s.Service.GetDraftAsync(h.WallId))!.Plan!.Revision);
        Assert.False(unknown.Accepted);
        Assert.Contains("no marker plan revision 9", unknown.Errors.Single());
    }

    /// <summary>The Attic today (legacy model, no plan), then revision 1 from the measured wall and a revision 2.</summary>
    private static async Task PrepareAsync(WallTestHarness h, CaptureScenario s, Func<MarkerPlan, MarkerPlan> revise)
    {
        var imported = await WallGlyphSettingsTests.Service(h).ImportGeometryAsync(h.WallId, Rev1Json, "legacy");
        Assert.True(imported.Succeeded, string.Join(" ", imported.Errors));
        var rev1 = await s.MarkerPlans.BuildFromMeasuredGeometryAsync(h.WallId, AtticMarkerPlan.Photo);
        var first = await s.MarkerPlans.SavePlanAsync(h.WallId, rev1!);
        var second = await s.MarkerPlans.SavePlanAsync(h.WallId, revise(rev1!));
        Assert.Equal((1, 2), (first.Revision!.Value, second.Revision!.Value));
    }

    /// <summary>A solve result whose cameras are named like the capture's photos (p00, p01).</summary>
    private static string Solved(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var cameras = root["cameras"]!.AsArray();
        for (var i = 0; i < cameras.Count; i++)
        {
            cameras[i]!["image"] = $"p{i:D2}";
        }

        return root.ToJsonString();
    }
}
