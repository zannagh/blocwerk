// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// The slow after-completion steps (<see cref="ICaptureFollowUpStep.RunsAfterCompletion"/>) beside the capture worker: the
/// capture marks its record (<see cref="CaptureFollowUpRecord.AfterCompletion"/>, a fresh id each time) and hands the capture to
/// <see cref="AfterCompletionWorker"/>, so the next capture (an update's 3D step) does not wait for a search of minutes. The
/// mark makes the run resumable: a restart queues it again, and a run that keeps bringing the process down is dropped after
/// <see cref="MaxRecoveries"/> starts like the other marks. A run clears only the mark it ran for: a capture completed again
/// meanwhile keeps its newer mark and runs again.
/// </summary>
public sealed partial class CaptureFollowUpChain
{
    /// <summary>Marks the capture's after-completion steps as queued (whatever model it points at). Null when the capture is gone.</summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The record as stored now, or null.</returns>
    public Task<CaptureFollowUpRecord?> MarkAfterCompletionAsync(Guid captureId, CancellationToken ct) =>
        CaptureFollowUpRecordStore.UpdateAsync(dbContextFactory.CreateDbContext, captureId, r => r with { AfterCompletion = Guid.NewGuid() }, ct);

    /// <summary>
    /// For a record marked <see cref="CaptureFollowUpRecord.AfterCompletion"/>: runs the after-completion phase (only for a
    /// completed capture whose model is the active one, as always), then clears that mark (not a newer one) and takes back
    /// the start it counted. A graceful shutdown leaves the mark and takes its start back, so a restart runs it again at no cost.
    /// </summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether a newer mark (the capture completed again while this ran) is left to run.</returns>
    public async Task<bool> RunAfterCompletionAsync(Guid captureId, CancellationToken ct)
    {
        var (_, record, _, modelId) = await LoadAsync(captureId, ct);
        if (record.AfterCompletion is not { } mark)
        {
            return false;
        }

        await CountedAsync(captureId, modelId, () => RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, ct), ct);

        // The start was counted on the record of this model: taken back there only (a re-pointed record has its own count).
        await UpdateRecordAsync(captureId, modelId, r => r with { Recoveries = Math.Max(0, r.Recoveries - 1) }, ct);
        var after = await CaptureFollowUpRecordStore.UpdateAsync(
            dbContextFactory.CreateDbContext, captureId, r => r.AfterCompletion == mark ? r with { AfterCompletion = null } : r, ct);
        return after?.AfterCompletion is not null;
    }
}
