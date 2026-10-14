// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// The slow after-completion steps (<see cref="ICaptureFollowUpStep.RunsAfterCompletion"/>) beside the capture worker: the
/// capture marks its record (<see cref="CaptureFollowUpRecord.AfterCompletion"/>) and hands the capture to
/// <see cref="AfterCompletionWorker"/>, so the next capture (an update's 3D step) does not wait for a search of minutes. The
/// mark makes the run resumable: a restart queues it again, and a run that keeps bringing the process down is dropped after
/// <see cref="MaxRecoveries"/> starts like the other marks.
/// </summary>
public sealed partial class CaptureFollowUpChain
{
    /// <summary>Marks the capture's after-completion steps as queued (whatever model it points at). Null when the capture is gone.</summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The record as stored now, or null.</returns>
    public Task<CaptureFollowUpRecord?> MarkAfterCompletionAsync(Guid captureId, CancellationToken ct) =>
        CaptureFollowUpRecordStore.UpdateAsync(dbContextFactory.CreateDbContext, captureId, r => r with { AfterCompletion = true }, ct);

    /// <summary>
    /// For a record marked <see cref="CaptureFollowUpRecord.AfterCompletion"/>: runs the after-completion phase (only for a
    /// completed capture whose model is the active one, as always), then clears the mark. A graceful shutdown leaves the mark
    /// and takes its start back, so a restart runs it again at no cost.
    /// </summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RunAfterCompletionAsync(Guid captureId, CancellationToken ct)
    {
        var (_, record, _, modelId) = await LoadAsync(captureId, ct);
        if (!record.AfterCompletion)
        {
            return;
        }

        await CountedAsync(captureId, modelId, () => RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, ct), ct);
        await CaptureFollowUpRecordStore.UpdateAsync(
            dbContextFactory.CreateDbContext, captureId, r => r with { AfterCompletion = false, Recoveries = 0 }, ct);
    }
}
