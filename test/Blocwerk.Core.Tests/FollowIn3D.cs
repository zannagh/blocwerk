// <copyright file="FollowIn3D.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Helpers of the 3D-follows-2D tests on <see cref="HoldPlacementScenario"/>.</summary>
internal static class FollowIn3D
{
    /// <summary>Eight holds on photo c0, placed by an admin run (their registration is then cached).</summary>
    public static async Task<List<Guid>> SeedPlacedAsync(HoldPlacementScenario s)
    {
        var ids = new List<Guid>();
        foreach (var (x, y) in new[] { (0.1, 0.2), (0.2, 0.7), (0.35, 0.4), (0.4, 0.8), (0.15, 0.5), (0.6, 0.3), (0.7, 0.6), (0.85, 0.4) })
        {
            ids.Add(await s.AddHoldAsync(x, y, configure: hold => hold.ShapePoints = ShapePoint.DefaultOctagon(0.01)));
        }

        await s.Service().PlaceAsync(s.Harness.WallId);
        return ids;
    }

    /// <summary>A pending proposal on facet "0" at (a, b), 10 mm off the wall.</summary>
    public static async Task<Guid> ProposalAsync(HoldPlacementScenario s, double a, double b)
    {
        await using var db = s.Harness.CreateContext();
        var p = new HoldProposal
        {
            WallId = s.Harness.WallId, GeometryModelId = s.ModelId, FacetId = "0", A = a, B = b, H = 10, X = a, Y = -10, Z = b,
            SizeMm = 50, Views = 3, Confidence = 0.8, BestPhoto = "p.jpg",
        };
        db.HoldProposals.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    public static WallService Walls(WallTestHarness h, IHoldRefinementQueue queue) => new(
        h.DbContextFactory, h.CurrentUser, h.HoldDetection, h.ActivityLog, NullLogger<WallService>.Instance, refinementQueue: queue);

    public static HoldProposalService Proposals(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, h.WallService, [], NullLogger<HoldProposalService>.Instance);

    public static HoldRefinementQueue Queue(WallTestHarness h, IHoldTexturePlacementService? placement)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(h.DbContextFactory);
        if (placement is not null)
        {
            services.AddSingleton(placement);
        }

        return new HoldRefinementQueue(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<HoldRefinementQueue>.Instance);
    }

    public static async Task<List<HoldPlacementRun>> RunsAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.HoldPlacementRuns.AsNoTracking().ToListAsync();
    }

    public static HoldEdit Edit(double x, double y) =>
        HoldEdit.FromEditorState(x, y, 0.01, null, HoldCategory.Hand, false, ShapePoint.DefaultOctagon(0.01), null, null, null, flagBouldersOnMove: false);
}
