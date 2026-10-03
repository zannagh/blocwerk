// <copyright file="CoverageHoldBoundsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Placed holds widen a facet's coverage region only on the wall's active model (their facet positions are in its
/// frame), and a hold edit that moves a facet's hold bounds makes the stored report stale.
/// </summary>
public class CoverageHoldBoundsTests
{
    [Fact]
    public async Task HoldBounds_ComeOnlyWithTheWallsActiveModel()
    {
        using var h = new WallTestHarness();
        var (_, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        await AddHoldAsync(h, "0", 6000, 1000);
        await AddHoldAsync(h, "0", 200, 300);

        IReadOnlyDictionary<string, PlaneRectMm> active, inactive;
        await using (var db = h.CreateContext())
        {
            active = await CoverageHoldBounds.LoadAsync(db, modelId, default);
        }

        await DeactivateAsync(h, modelId);
        await using (var db = h.CreateContext())
        {
            inactive = await CoverageHoldBounds.LoadAsync(db, modelId, default);
        }

        Assert.Equal(new PlaneRectMm(200, 6000, 300, 1000), active["0"]);
        Assert.Empty(inactive);
    }

    [Fact]
    public async Task AHoldEditThatMovesTheBounds_MakesTheReportStale()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        await CoverageReportFollowUpStepTests.SetDoneAsync(h, captureId);
        var service = CoverageReportFollowUpStepTests.Service(h);
        await service.ComputeFromPipelineAsync(captureId);
        Assert.NotNull((await service.GetAsync(h.WallId, captureId)).Report);

        await AddHoldAsync(h, "0", 1500, 1200);
        var stale = await service.GetAsync(h.WallId, captureId);

        Assert.Null(stale.Report);
        Assert.True(stale.Computing);
        await (CoverageBackgroundCompute.Pending(captureId) ?? Task.CompletedTask);
        Assert.NotNull((await service.GetAsync(h.WallId, captureId)).Report);
    }

    private static async Task AddHoldAsync(WallTestHarness h, string facetId, double a, double b)
    {
        await using var db = h.CreateContext();
        db.Holds.Add(new Hold { WallId = h.WallId, X = 0.5, Y = 0.5, FacetId = facetId, PlaneAMm = a, PlaneBMm = b });
        await db.SaveChangesAsync();
    }

    private static async Task DeactivateAsync(WallTestHarness h, Guid modelId)
    {
        await using var db = h.CreateContext();
        (await db.WallGeometryModels.SingleAsync(m => m.Id == modelId)).IsActive = false;
        await db.SaveChangesAsync();
    }
}
