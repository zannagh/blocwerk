// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Writing the record: several background services write a capture's <see cref="Entities.WallCapture.FollowUpJson"/>
/// (the capture, re-solve, re-render and correction workers), so every write here is conditional: it lands only while
/// the capture still points at the model the run worked on AND the record is still the one it read (else it reads again
/// and merges again). A capture re-pointed meanwhile (corrected, solved again) has a fresh record a stale run must not
/// overwrite.
/// </summary>
public sealed partial class CaptureFollowUpChain
{
    /// <summary>Starts in a row that may pick up an unfinished mark before it is dropped (a crash in it must not loop).</summary>
    public const int MaxRecoveries = 3;

    private const int MaxWriteAttempts = 20;

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

        foreach (var phase in AllPhases)
        {
            await RunAsync(captureId, phase, ct);
        }

        await UpdateRecordAsync(captureId, modelId, r => r with { RunAgain = false, Recoveries = 0 }, ct);
    }

    /// <summary>
    /// Counts one startup recovery of the capture's <paramref name="kind"/> mark. False (and the mark dropped with a note)
    /// once <see cref="MaxRecoveries"/> starts in a row picked it up without it finishing; else true: queue it.
    /// </summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="kind">The mark.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether to queue the work.</returns>
    public async Task<bool> CountRecoveryAsync(Guid captureId, CaptureFollowUpRecoveryKind kind, CancellationToken ct)
    {
        Guid? modelId;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            modelId = await db.WallCaptures.AsNoTracking().Where(c => c.Id == captureId).Select(c => c.GeometryModelId).FirstOrDefaultAsync(ct);
        }

        var queue = false;
        var saved = await UpdateRecordAsync(
            captureId,
            modelId,
            r =>
            {
                var marked = kind == CaptureFollowUpRecoveryKind.Rederive ? r.Rederive : r.RunAgain;
                queue = marked && r.Recoveries < MaxRecoveries;
                if (queue || !marked)
                {
                    return queue ? r with { Recoveries = r.Recoveries + 1 } : r;
                }

                logger.LogWarning("Capture {CaptureId}: its follow-up steps did not finish in {Count} starts; they are dropped", captureId, r.Recoveries);
                const string GaveUp = "The follow-up steps were stopped after several restarts in a row did not finish them (the server stopped in them each time).";
                var dropped = kind == CaptureFollowUpRecoveryKind.Rederive ? r with { Rederive = false } : r with { RunAgain = false };
                return dropped with { Recoveries = 0, Note = r.Note is null ? GaveUp : $"{r.Note} {GaveUp}" };
            },
            ct);
        return saved is not null && queue;
    }

    /// <summary>
    /// Merges <paramref name="entry"/> into the stored record. Null (nothing written) when the capture no longer points
    /// at the context's model; else the record as stored now.
    /// </summary>
    private Task<CaptureFollowUpRecord?> SaveEntryAsync(CaptureFollowUpContext context, CaptureFollowUpEntry entry, CancellationToken ct) =>
        UpdateRecordAsync(context.CaptureId, context.ModelId, r => r.With(entry), ct);

    /// <summary>
    /// Applies <paramref name="change"/> to the stored record with a conditional write (the capture still points at
    /// <paramref name="modelId"/> and its record is unchanged since it was read), reading again when another writer came
    /// first. Null when the capture is gone or points at another model.
    /// </summary>
    private async Task<CaptureFollowUpRecord?> UpdateRecordAsync(
        Guid captureId, Guid? modelId, Func<CaptureFollowUpRecord, CaptureFollowUpRecord> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            await using var db = dbContextFactory.CreateDbContext();
            var row = await db.WallCaptures.AsNoTracking()
                .Where(c => c.Id == captureId)
                .Select(c => new { c.GeometryModelId, c.FollowUpJson })
                .FirstOrDefaultAsync(ct);
            if (row is null || row.GeometryModelId != modelId)
            {
                logger.LogInformation(
                    "Capture {CaptureId}: no longer points at model {ModelId}; its follow-up record is left to the new model's run", captureId, modelId);
                return null;
            }

            var read = row.FollowUpJson;
            var record = change(CaptureFollowUpRecord.Parse(read));
            var json = record.ToJson();
            if (BeforeConditionalWrite is { } hook)
            {
                await hook();
            }

            var written = await db.WallCaptures
                .Where(c => c.Id == captureId && c.GeometryModelId == modelId && c.FollowUpJson == read)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FollowUpJson, json), ct);
            if (written == 1)
            {
                return record;
            }
        }

        throw new InvalidOperationException($"The follow-up record of capture {captureId} kept changing; it was not written.");
    }
}
