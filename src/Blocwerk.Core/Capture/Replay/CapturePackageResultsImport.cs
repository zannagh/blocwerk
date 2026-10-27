// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The commit's use of a package's <see cref="CapturePackageResults"/>, inside its transaction: the finished view, the volumes,
/// and each live hold's 3D fields where the hold here has the same id and panel geometry as on the source. Writes only 3D
/// fields, volumes, proposals and a revertable <see cref="HoldPlacementRun"/> (trigger <see cref="HoldPlacementTrigger.Import"/>,
/// so the automatic placement sees those holds as placed on the model); a hold's panel position, outline, links, boulders
/// and review state are never touched. The live holds without a matching result are returned for the refinement queue.
/// </summary>
/// <remarks>
/// Proposals exclude the holds known when they were found. The source's pending proposals are taken unless the target
/// already has them (shared history) or one overlaps, on its panel photo, a hold that differs here or exists only here: the
/// search here would not propose a known hold. A spot freed by a hold deleted here is not proposed (the admin's
/// "find holds" search covers it).
/// </remarks>
internal sealed class CapturePackageResultsImport(CapturePackageResults results, List<Hold> live, HashSet<Guid> knownProposals)
{
    /// <summary>Loads the wall's live holds (tracked) and which carried proposals the target already has.</summary>
    public static async Task<CapturePackageResultsImport> PrepareAsync(
        BlocwerkDbContext db, Guid wallId, CapturePackageResults results, CancellationToken ct)
    {
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).ToListAsync(ct);
        var ids = results.Proposals.Select(p => p.Id).ToList();
        var known = await db.HoldProposals.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        return new CapturePackageResultsImport(results, live, [.. known]);
    }

    /// <summary>Adds the rows and applies the matching holds' 3D fields; returns the ids of the live holds left to compute.</summary>
    public List<Guid> Apply(BlocwerkDbContext db, WallGeometryModel model, IEnumerable<WallGeometryTexture> textures, Guid userId)
    {
        db.WallGeometrySplats.Add(results.Splat);
        db.WallVolumes.AddRange(results.Volumes);
        var carried = results.Holds.GroupBy(r => r.HoldId).ToDictionary(g => g.Key, g => g.First());
        var entries = new List<HoldPlacementEntry>();
        var unmatched = new List<Hold>();
        foreach (var hold in live)
        {
            if (!carried.TryGetValue(hold.Id, out var result) || result.Geometry != HoldPlacementEntry.HashGeometry(hold))
            {
                unmatched.Add(hold);
                continue;
            }

            var before = HoldPlacementEntry.Before(hold) with { RestoresVolumePlacement = true, PrevVolumePlacementJson = hold.VolumePlacementJson };
            result.ApplyTo(hold);
            entries.Add(before with
            {
                PlacementHash = HoldPlacementEntry.HashPlacement(hold),
                FingerprintHash = HoldPlacementEntry.HashFingerprint(hold.FingerprintJson),
                GeometryHash = result.Geometry,
            });
        }

        db.HoldProposals.AddRange(results.Proposals.Where(p => !knownProposals.Contains(p.Id) && !unmatched.Any(h => Overlaps(h, p))));
        db.HoldPlacementRuns.Add(new HoldPlacementRun
        {
            WallId = model.WallId,
            GeometryModelId = model.Id,
            TextureSetKey = TextureSetStamp.KeyOf(textures.Select(t => (t.FacetId, t.StoredPath))),
            CreatedByUserId = userId,
            Trigger = HoldPlacementTrigger.Import,
            HoldsJson = HoldPlacementEntry.ToJson(entries),
            PlacedCount = live.Except(unmatched).Count(h => h is { FacetId: not null, PlaneAMm: not null, PlaneBMm: not null }),
        });
        return unmatched.Select(h => h.Id).ToList();
    }

    /// <summary>Whether the proposal's panel point overlaps the hold on the same panel photo (normalised centres and radii).</summary>
    private static bool Overlaps(Hold hold, HoldProposal proposal) =>
        proposal is { PanelId: { } panel, PanelX: { } x, PanelY: { } y }
        && panel == hold.WallPanelId
        && Math.Sqrt(Math.Pow(x - hold.X, 2) + Math.Pow(y - hold.Y, 2)) <= hold.Radius + (proposal.PanelRadius ?? 0);
}
