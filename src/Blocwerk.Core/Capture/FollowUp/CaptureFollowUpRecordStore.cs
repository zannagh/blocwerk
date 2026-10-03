// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// The one way to change a capture's <see cref="Entities.WallCapture.FollowUpJson"/>: several background services write
/// it (the capture, re-solve, re-render and correction workers, the retrain request), so a change is a conditional write
/// that lands only while the record (and the model the capture points at) is still what was read; else it reads again and
/// applies the change again. Each write stamps <see cref="Entities.WallCapture.UpdatedAt"/> (the progress API reads follow-up
/// activity from it).
/// </summary>
internal static class CaptureFollowUpRecordStore
{
    private const int MaxAttempts = 20;

    /// <summary>
    /// Applies <paramref name="change"/> while the capture points at <paramref name="modelId"/>. Null (nothing written)
    /// when the capture is gone or points at another model; else the record as stored now.
    /// </summary>
    public static Task<CaptureFollowUpRecord?> UpdateForModelAsync(
        Func<BlocwerkDbContext> createDb,
        Guid captureId,
        Guid? modelId,
        Func<CaptureFollowUpRecord, CaptureFollowUpRecord> change,
        CancellationToken ct,
        Func<Task>? beforeWrite = null) =>
        WriteAsync(createDb, captureId, read => read == modelId, change, beforeWrite, ct);

    /// <summary>Applies <paramref name="change"/> whatever model the capture points at. Null when the capture is gone.</summary>
    public static Task<CaptureFollowUpRecord?> UpdateAsync(
        Func<BlocwerkDbContext> createDb, Guid captureId, Func<CaptureFollowUpRecord, CaptureFollowUpRecord> change, CancellationToken ct) =>
        WriteAsync(createDb, captureId, _ => true, change, null, ct);

    private static async Task<CaptureFollowUpRecord?> WriteAsync(
        Func<BlocwerkDbContext> createDb,
        Guid captureId,
        Func<Guid?, bool> modelAccepted,
        Func<CaptureFollowUpRecord, CaptureFollowUpRecord> change,
        Func<Task>? beforeWrite,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await using var db = createDb();
            var row = await db.WallCaptures.AsNoTracking()
                .Where(c => c.Id == captureId)
                .Select(c => new { c.GeometryModelId, c.FollowUpJson })
                .FirstOrDefaultAsync(ct);
            if (row is null || !modelAccepted(row.GeometryModelId))
            {
                return null;
            }

            var (model, read) = (row.GeometryModelId, row.FollowUpJson);
            var before = CaptureFollowUpRecord.Parse(read);
            var record = change(before);
            var json = record.ToJson();
            if (json == before.ToJson())
            {
                // Nothing to change (and a missing record stays missing).
                return record;
            }

            if (beforeWrite is not null)
            {
                await beforeWrite();
            }

            var written = await db.WallCaptures
                .Where(c => c.Id == captureId && c.GeometryModelId == model && c.FollowUpJson == read)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FollowUpJson, json).SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow), ct);
            if (written == 1)
            {
                return record;
            }
        }

        throw new InvalidOperationException($"The follow-up record of capture {captureId} kept changing; it was not written.");
    }
}
