// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The source side of a package with computed results: a live panel with three holds whose 3D fields were computed on the
/// capture's model, a volume, two pending proposals, a boulder and hold links. <see cref="ForgetAsync"/> makes the same
/// instance the target: the capture is forgotten and the holds have no 3D fields and the wall no proposals yet.
/// </summary>
internal sealed record CapturePackageResultsSeed(Guid PanelId, IReadOnlyList<Guid> HoldIds, Guid FreeSpotProposalId, Guid FarProposalId)
{
    /// <summary>Where the first proposal is (normalised panel point): a spot no hold covers on the source.</summary>
    public const double FreeX = 0.4;

    public const double FreeY = 0.8;

    public static async Task<CapturePackageResultsSeed> CreateAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var modelId = await db.WallGeometryModels.Where(m => m.IsActive).Select(m => m.Id).SingleAsync();
        var panel = new WallPanel { WallId = h.WallId, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" };
        var holds = Enumerable.Range(0, 3).Select(i => new Hold
        {
            WallId = h.WallId, WallPanelId = panel.Id, X = 0.2 + (0.2 * i), Y = 0.5, Radius = 0.03,
            FacetId = "f1", PlaneAMm = 100 * i, PlaneBMm = 50, WidthMm = 40, HeightMm = 30, AreaMm2 = 900, MetricSource = "texture",
            FootprintMm = $"{{\"n\":{i}}}", ProtrusionMm = "{\"mm\":20}", FingerprintJson = "{\"hue\":1}",
        }).ToList();
        var boulder = new Boulder { WallId = h.WallId, Name = "B", CreatedByUserId = h.Owner.Id };
        var free = Proposal(h.WallId, modelId, panel.Id, FreeX, FreeY);
        var far = Proposal(h.WallId, modelId, panel.Id, 0.9, 0.1);
        db.WallPanels.Add(panel);
        db.Holds.AddRange(holds);
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holds[0].Id });
        db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = holds[0].Id, HoldBId = holds[2].Id });
        db.HoldGenerationLinks.Add(new HoldGenerationLink { WallId = h.WallId, OldHoldId = holds[2].Id, NewHoldId = holds[1].Id });
        db.WallVolumes.Add(new WallVolume { WallId = h.WallId, GeometryModelId = modelId, FacetId = "f1", FootprintJson = "[]", SurfaceJson = "{}" });
        db.HoldProposals.AddRange(free, far);
        await db.SaveChangesAsync();
        return new CapturePackageResultsSeed(panel.Id, holds.Select(x => x.Id).ToList(), free.Id, far.Id);
    }

    /// <summary>The target before the import: the capture forgotten, no 3D fields on the holds, no proposals.</summary>
    public static async Task ForgetAsync(CapturePackageFlow f, CapturePackageManifest manifest)
    {
        await f.ForgetAsync(manifest);
        await using var db = f.Harness.CreateContext();
        await db.HoldProposals.ExecuteDeleteAsync();
        await db.Holds.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.FacetId, (string?)null).SetProperty(x => x.PlaneAMm, (double?)null).SetProperty(x => x.PlaneBMm, (double?)null)
            .SetProperty(x => x.WidthMm, (double?)null).SetProperty(x => x.HeightMm, (double?)null).SetProperty(x => x.AreaMm2, (double?)null)
            .SetProperty(x => x.MetricSource, (string?)null).SetProperty(x => x.FootprintMm, (string?)null)
            .SetProperty(x => x.ProtrusionMm, (string?)null).SetProperty(x => x.FingerprintJson, (string?)null));
    }

    /// <summary>
    /// Everything the import must never change, as text: panels (images), holds without their 3D fields (positions, outlines,
    /// review state), hold links, generation links, boulders and their holds.
    /// </summary>
    public static async Task<string> PanelTruthAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var holds = await db.Holds.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        foreach (var hold in holds)
        {
            new CapturePackageHoldResult { Geometry = string.Empty }.ApplyTo(hold);
        }

        return JsonSerializer.Serialize(new object[]
        {
            await db.WallPanels.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            holds,
            await db.HoldLinks.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            await db.HoldGenerationLinks.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            await db.Boulders.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            await db.BoulderHolds.AsNoTracking().OrderBy(x => x.HoldId).ToListAsync(),
        });
    }

    private static HoldProposal Proposal(Guid wallId, Guid modelId, Guid panelId, double x, double y) => new()
    {
        WallId = wallId, GeometryModelId = modelId, FacetId = "f1", BestPhoto = "p01", Status = HoldProposalStatus.Pending,
        PanelId = panelId, PanelX = x, PanelY = y, PanelRadius = 0.02,
    };
}
