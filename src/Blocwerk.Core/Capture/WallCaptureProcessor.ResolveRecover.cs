// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A re-solve a previous process left between storing the new model and activating it: the model is found by its source
/// and the solve job named in its notes (<see cref="WallCaptureProcessor.ResolvedModelNotes"/>), so the run goes on to
/// the adoption instead of solving (and storing) again.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>The outcome of the run's already stored model; null when its job stored none yet.</summary>
    private async Task<ResolveOutcome?> StoredResolveAsync(CaptureRun run, CancellationToken ct)
    {
        var capture = run.Capture;
        if (CaptureResolveMark.JobId(capture.SolveJobId) is not { } jobId)
        {
            return null;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var source = ResolvedModelSource(capture.Id);
        var notes = ResolvedModelNotes(capture.Id, jobId);
        var stored = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == capture.WallId && m.Source == source && m.Notes == notes && !m.IsActive)
            .Select(m => new { m.Id, m.Json })
            .FirstOrDefaultAsync(ct);
        if (stored is null)
        {
            return null;
        }

        logger.LogInformation("Capture {CaptureId}: resuming the re-solve from its stored model {ModelId}", capture.Id, stored.Id);
        return new ResolveOutcome(stored.Id, jobId, StoredRefusal(stored.Json), stored.Json);
    }

    /// <summary>Why a stored re-solved model may not be activated, read from the document (the registration it carries).</summary>
    private static string? StoredRefusal(string json) =>
        RegisteredGeometry.Carried(json).ReferenceModelId is null
            ? "It could not be tied to the active model."
            : ResolveRefusal(new FrameOutcome(json, true, null), json);
}
