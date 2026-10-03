// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A re-solve a previous process left between storing the new model and activating it: the model is found by its source
/// and the solve job named in its notes (<see cref="WallCaptureProcessor.ResolvedModelNotes"/>), so the run goes on to
/// the adoption instead of solving (and storing) again. The verdict is the one the fresh solve reached on the solved
/// document (written into the notes after <see cref="WallCaptureProcessor.RefusedNote"/>), not re-checked on the stored,
/// registered one (which may carry facets and markers of the model it was tied to); the placement check was stored
/// before the model was.
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
        var accepted = ResolvedModelNotes(capture.Id, jobId);
        var refused = accepted + RefusedNote;
        var stored = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == capture.WallId && m.Source == source && !m.IsActive
                        && (m.Notes == accepted || (m.Notes != null && m.Notes.StartsWith(refused))))
            .Select(m => new { m.Id, m.Notes })
            .FirstOrDefaultAsync(ct);
        if (stored is null)
        {
            return null;
        }

        var refusal = stored.Notes!.Length > accepted.Length ? stored.Notes[refused.Length..] : null;
        logger.LogInformation(
            "Capture {CaptureId}: resuming the re-solve from its stored model {ModelId} (refused: {Refused})", capture.Id, stored.Id, refusal is not null);
        return new ResolveOutcome(stored.Id, jobId, refusal);
    }
}
