// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// What a package carries of its source's history, and how the target maps it onto its own:
/// <list type="bullet">
/// <item>a capture whose model was solved again (<see cref="WallCaptureProcessor.ResolveModelAsync"/>) kept the trained view
/// of its first model (the same <c>.spz</c> and frame, shared with the new model); the export binds the view's GPU job to
/// the capture's current model, which the target then installs the finished view on;</item>
/// <item>the marker plan is matched by its content, not by its revision number (the target numbers its own revisions);</item>
/// <item>the model's registration to an earlier model the target does not have is kept only as history
/// (<see cref="RegisteredGeometry.DetachFromReference"/>): the model becomes the target's frame as it is.</item>
/// </list>
/// </summary>
public sealed partial class CapturePackageService
{
    /// <summary>
    /// Binds the capture's trained view (its GPU job) to the capture's model when the job was trained for an earlier model of
    /// the same capture whose view the current model shares (a re-solve); refuses a job whose view the model does not share.
    /// </summary>
    private static async Task BindViewToModelAsync(
        BlocwerkDbContext db, GpuJob job, WallGeometryModel model, List<string> warnings, CancellationToken ct)
    {
        if (job.GeometryModelId == model.Id)
        {
            return;
        }

        var trainedFor = job.GeometryModelId;
        var shared = await db.WallGeometrySplats.AsNoTracking()
            .Where(s => s.GeometryModelId == model.Id)
            .AnyAsync(s => db.WallGeometrySplats.Any(t => t.GeometryModelId == trainedFor && t.StoredPath == s.StoredPath), ct);
        if (!shared)
        {
            throw new UserFacingException(
                $"The capture's trained photo-real view belongs to model {trainedFor}, not to its current model {model.Id}, which does not share that view, so it cannot be replayed without training.");
        }

        job.GeometryModelId = model.Id;
        warnings.Add(
            $"The capture's model {model.Id} was solved again after training; the trained view (trained for model {trainedFor}, same frame) is carried for it.");
    }

    /// <summary>
    /// Whether the package's trained view belongs to its model: trained for it, or (a package from a source that exported
    /// a re-solved capture as it is) trained for an earlier model of the capture that the model was solved again from
    /// and registered to, i.e. in the same frame (<see cref="WallCaptureProcessor.ResolveModelAsync"/> activates only such).
    /// </summary>
    internal static bool ViewFitsModel(CapturePackageManifest m)
    {
        var (model, job) = (m.Rows.Model, m.Rows.GpuJob);
        return job.GeometryModelId == model.Id
               || (model.Source == WallCaptureProcessor.ResolvedModelSource(m.CaptureId)
                   && RegisteredGeometry.Carried(model.Json).ReferenceModelId is not null
                   && !FrameLineage.IsReset(model.Json));
    }

    /// <summary>
    /// The target's revision of the capture's marker plan: the one with the same number when its plan is the same (or the
    /// package carries no plan), else the newest revision whose plan is the same; <c>Same</c> false when only a revision
    /// with the same number but another plan exists; <c>Revision</c> null when none fits.
    /// </summary>
    private static async Task<(int? Revision, bool Same)> TargetPlanRevisionAsync(BlocwerkDbContext db, CapturePackageManifest m, CancellationToken ct)
    {
        if (m.PlanRevision is not { } revision)
        {
            return (null, true);
        }

        var plans = await db.WallMarkerPlans.IgnoreQueryFilters().AsNoTracking().Where(p => p.WallId == m.WallId)
            .Select(p => new { p.Revision, p.Json }).ToListAsync(ct);
        var numbered = plans.FirstOrDefault(p => p.Revision == revision);
        if (numbered is not null && (m.PlanJson is null || SameJson(numbered.Json, m.PlanJson)))
        {
            return (revision, true);
        }

        var byContent = m.PlanJson is null
            ? null
            : plans.Where(p => SameJson(p.Json, m.PlanJson)).MaxBy(p => p.Revision);
        if (byContent is not null)
        {
            return (byContent.Revision, true);
        }

        return (numbered?.Revision, false);
    }

    /// <summary>The plan problems of <see cref="TargetPlanRevisionAsync"/>'s answer.</summary>
    private static async Task PlanProblemsAsync(
        BlocwerkDbContext db, CapturePackageManifest m, List<string> blockers, List<string> warnings, CancellationToken ct)
    {
        if (m.PlanRevision is not { } revision)
        {
            return;
        }

        var (here, same) = await TargetPlanRevisionAsync(db, m, ct);
        if (here is null)
        {
            blockers.Add(
                $"Marker plan revision {revision} of the wall does not exist here, and no revision here has the same plan: attach the capture's plan here first (the same plan file).");
        }
        else if (!same)
        {
            warnings.Add($"Marker plan revision {revision} differs here from the source's.");
        }
        else if (here != revision)
        {
            warnings.Add($"The capture's marker plan (revision {revision} on the source) is revision {here} here (same plan); the capture and its model are stored with revision {here}.");
        }
    }
}
