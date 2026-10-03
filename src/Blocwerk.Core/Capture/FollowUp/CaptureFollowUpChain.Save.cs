// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Writing the record: several background services write a capture's <see cref="Entities.WallCapture.FollowUpJson"/>
/// (the capture, re-solve, re-render and correction workers), so every write merges into the record as stored now, and
/// only while the capture still points at the model the run worked on. A capture re-pointed meanwhile (corrected, solved
/// again) has a fresh record of its own that a stale run must not overwrite.
/// </summary>
public sealed partial class CaptureFollowUpChain
{
    private static readonly CaptureFollowUpPhase[] AllPhases =
        [CaptureFollowUpPhase.Model, CaptureFollowUpPhase.Final, CaptureFollowUpPhase.AfterCompletion];

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

        await ClearMarkAsync(captureId, modelId, r => r with { RunAgain = false }, ct);
    }

    /// <summary>
    /// Merges <paramref name="entry"/> into the stored record. Null (nothing written) when the capture no longer points
    /// at the context's model; else the record as stored now.
    /// </summary>
    private async Task<CaptureFollowUpRecord?> SaveEntryAsync(CaptureFollowUpContext context, CaptureFollowUpEntry entry, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = await db.WallCaptures.FirstAsync(c => c.Id == context.CaptureId, ct);
        if (capture.GeometryModelId != context.ModelId)
        {
            logger.LogInformation(
                "Capture {CaptureId}: now points at model {ModelId}, not {RunModelId}; follow-up {Step} is not recorded",
                context.CaptureId, capture.GeometryModelId, context.ModelId, entry.Key);
            return null;
        }

        var record = CaptureFollowUpRecord.Parse(capture.FollowUpJson).With(entry);
        capture.FollowUpJson = record.ToJson();
        await db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>Applies <paramref name="clear"/> to the stored record while the capture still points at <paramref name="modelId"/>.</summary>
    private async Task ClearMarkAsync(Guid captureId, Guid? modelId, Func<CaptureFollowUpRecord, CaptureFollowUpRecord> clear, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == captureId, ct);
        if (capture is null || capture.GeometryModelId != modelId)
        {
            return;
        }

        capture.FollowUpJson = clear(CaptureFollowUpRecord.Parse(capture.FollowUpJson)).ToJson();
        await db.SaveChangesAsync(ct);
    }
}
