// <copyright file="WallCaptureProcessor.Revision.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Stage 2a: the photos must show the plan revision the capture was solved with (<see cref="CaptureRevisionCheck"/>);
/// a saved-but-not-yet-swapped revision is refused before its model is stored or tied to the active one.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task CheckShownRevisionAsync(CaptureRun run, string solvedJson, CancellationToken ct)
    {
        var capture = run.Capture;
        if (capture.PlanJson is null || capture.PlanRevision is not { } revision || run.Layout.Plan is not { } plan
            || TryParse(solvedJson) is not { } solved)
        {
            return;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var saved = await MarkerRevisionCandidates.LoadAsync(db, capture.WallId, ct);

        // The capture's own snapshot is the truth for its side (it is what the solver was told).
        var own = new RevisionCandidate(revision, plan, saved.FirstOrDefault(c => c.Revision == revision)?.EffectiveFrom);
        var candidates = saved.Where(c => c.Revision != revision).Append(own).OrderBy(c => c.Revision).ToList();
        if (CaptureRevisionCheck.Refusal(solved, revision, candidates, DateTimeOffset.UtcNow) is { } refusal)
        {
            logger.LogWarning("Capture {CaptureId}: photos show another marker revision than {Revision}: {Reason}", capture.Id, revision, refusal);
            throw new CaptureFailedException(refusal);
        }
    }
}
