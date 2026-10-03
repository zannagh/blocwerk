// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Writing the record (<see cref="CaptureFollowUpRecordStore"/>: conditional, only while the capture still points at the
/// model the run worked on, so a capture re-pointed meanwhile keeps its fresh record), and the resumable marks: a run of
/// one counts as started until it finishes, so a mark whose runs keep bringing the process down is dropped at a start.
/// </summary>
public sealed partial class CaptureFollowUpChain
{
    /// <summary>Runs of a mark that may start without finishing before a startup drops it (a crash in it must not loop).</summary>
    public const int MaxRecoveries = 3;

    /// <summary>The note left on a capture whose follow-up mark was dropped.</summary>
    public const string GaveUpNote = "The follow-up steps were stopped after several restarts in a row did not finish them (the server stopped in them each time).";

    private static readonly CaptureFollowUpPhase[] AllPhases =
        [CaptureFollowUpPhase.Model, CaptureFollowUpPhase.Final, CaptureFollowUpPhase.AfterCompletion];

    /// <summary>Runs between reading the record and the conditional write (tests make a concurrent writer here).</summary>
    internal Func<Task>? BeforeConditionalWrite { get; set; }

    /// <summary>
    /// For a record marked <see cref="CaptureFollowUpRecord.RunAgain"/> (the capture was re-pointed at a corrected or
    /// re-activated model): runs every phase of the chain in order on the model it now points at, then clears the mark.
    /// The mark is also cleared when that model is not the active one (nothing to run for it).
    /// </summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation (the mark stays, so a restart runs the chain again).</param>
    /// <returns>A task.</returns>
    public async Task RunAgainAsync(Guid captureId, CancellationToken ct)
    {
        var (_, record, _, modelId) = await LoadAsync(captureId, ct);
        if (!record.RunAgain)
        {
            return;
        }

        await CountedAsync(
            captureId,
            modelId,
            async () =>
            {
                foreach (var phase in AllPhases)
                {
                    await RunAsync(captureId, phase, ct);
                }
            },
            ct);
        await UpdateRecordAsync(captureId, modelId, r => r with { RunAgain = false, Recoveries = 0 }, ct);
    }

    /// <summary>
    /// Whether a startup may queue the capture's <paramref name="kind"/> mark: false (and the mark dropped with a note)
    /// once <see cref="MaxRecoveries"/> runs of it started without finishing.
    /// </summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="kind">The mark.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether to queue the work.</returns>
    public async Task<bool> MayRecoverAsync(Guid captureId, CaptureFollowUpRecoveryKind kind, CancellationToken ct)
    {
        var queue = false;
        var saved = await CaptureFollowUpRecordStore.UpdateAsync(
            dbContextFactory.CreateDbContext,
            captureId,
            r =>
            {
                var marked = kind == CaptureFollowUpRecoveryKind.Rederive ? r.Rederive : r.RunAgain;
                queue = marked && r.Recoveries < MaxRecoveries;
                if (queue || !marked)
                {
                    return r;
                }

                logger.LogWarning("Capture {CaptureId}: {Count} runs of its follow-up steps did not finish; they are dropped", captureId, r.Recoveries);
                var dropped = kind == CaptureFollowUpRecoveryKind.Rederive ? r with { Rederive = false } : r with { RunAgain = false };
                return dropped with { Recoveries = 0, Running = null, Note = r.Note is null ? GaveUpNote : $"{r.Note} {GaveUpNote}" };
            },
            ct);
        return saved is not null && queue;
    }

    /// <summary>
    /// Runs <paramref name="work"/> counted as a started run of the record's mark (<see cref="CaptureFollowUpRecord.Recoveries"/>):
    /// a graceful shutdown in it takes the count back, a crash leaves it.
    /// </summary>
    private async Task CountedAsync(Guid captureId, Guid? modelId, Func<Task> work, CancellationToken ct)
    {
        await UpdateRecordAsync(captureId, modelId, r => r with { Recoveries = r.Recoveries + 1 }, ct);
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await UpdateRecordAsync(captureId, modelId, r => r with { Recoveries = Math.Max(0, r.Recoveries - 1) }, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Merges <paramref name="entry"/> into the stored record; null (nothing written) once the capture was re-pointed.</summary>
    private async Task<CaptureFollowUpRecord?> SaveEntryAsync(CaptureFollowUpContext context, CaptureFollowUpEntry entry, CancellationToken ct)
    {
        var saved = await UpdateRecordAsync(context.CaptureId, context.ModelId, r => r.With(entry), ct);
        if (saved is null)
        {
            await DropOrphanedMarkAsync(context.CaptureId, entry, ct);
        }

        return saved;
    }

    private async Task<CaptureFollowUpRecord?> UpdateRecordAsync(
        Guid captureId, Guid? modelId, Func<CaptureFollowUpRecord, CaptureFollowUpRecord> change, CancellationToken ct)
    {
        var saved = await CaptureFollowUpRecordStore.UpdateForModelAsync(
            dbContextFactory.CreateDbContext, captureId, modelId, change, ct, BeforeConditionalWrite);
        if (saved is null)
        {
            logger.LogInformation(
                "Capture {CaptureId}: no longer points at model {ModelId}; its follow-up record is left to the new model's run", captureId, modelId);
        }

        return saved;
    }
}
