// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A re-solve or re-render run counts as started on the capture's follow-up record until it ends
/// (<see cref="CaptureFollowUpRecord.ResolveStarts"/>, <see cref="CaptureFollowUpRecord.RerenderStarts"/>); a graceful
/// shutdown takes the count back. A mark whose runs keep bringing the process down (or keep failing before they can end)
/// is dropped with a note once <see cref="MaxRedoStarts"/> runs started, instead of being queued again forever.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>Runs of a redo that may start without ending before its mark is dropped.</summary>
    internal const int MaxRedoStarts = CaptureFollowUpChain.MaxRecoveries;

    /// <summary>
    /// Whether the capture's marked <paramref name="kind"/> may be queued again: false (and the mark dropped with a note)
    /// once <see cref="MaxRedoStarts"/> runs of it started without ending.
    /// </summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="kind">The redo.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether to queue it.</returns>
    internal async Task<bool> MayQueueRedoAsync(Guid captureId, CaptureRedoKind kind, CancellationToken ct)
    {
        string? json;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            json = await db.WallCaptures.AsNoTracking().Where(c => c.Id == captureId).Select(c => c.FollowUpJson).FirstOrDefaultAsync(ct);
        }

        var record = CaptureFollowUpRecord.Parse(json);
        var starts = kind == CaptureRedoKind.Resolve ? record.ResolveStarts : record.RerenderStarts;
        if (starts < MaxRedoStarts)
        {
            return true;
        }

        logger.LogWarning("Capture {CaptureId}: {Count} runs of its {Kind} did not end; the mark is dropped", captureId, starts, kind);
        await ClearMarkAsync(
            captureId,
            c =>
            {
                if (kind == CaptureRedoKind.Resolve && CaptureResolveMark.IsResolving(c.SolveJobId))
                {
                    c.SolveJobId = null;
                }
                else if (kind == CaptureRedoKind.Rerender && CaptureTextureOutcome.IsRerendering(c.TexturesJobId))
                {
                    c.TexturesJobId = null;
                }
            },
            ct);
        var note = kind == CaptureRedoKind.Resolve
            ? "Solving the 3D model again was stopped: it did not finish in several tries (the server stopped in it each time). The active model stays."
            : "Rendering the wall textures again was stopped: it did not finish in several tries (the server stopped in it each time). The previous textures stay.";
        await NoteAsync(captureId, note, ct, r => WithStarts(r, kind, -int.MaxValue / 2));
        return false;
    }

    /// <summary>Runs <paramref name="work"/> counted as a started run of the capture's <paramref name="kind"/>.</summary>
    private async Task RedoCountedAsync(Guid captureId, CaptureRedoKind kind, Func<Task> work, CancellationToken ct)
    {
        await UpdateRecordAsync(captureId, r => WithStarts(r, kind, 1), ct);
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await UpdateRecordAsync(captureId, r => WithStarts(r, kind, -1), CancellationToken.None);
            throw;
        }
    }

    private static CaptureFollowUpRecord WithStarts(CaptureFollowUpRecord r, CaptureRedoKind kind, int delta) => kind == CaptureRedoKind.Resolve
        ? r with { ResolveStarts = Math.Max(0, r.ResolveStarts + delta) }
        : r with { RerenderStarts = Math.Max(0, r.RerenderStarts + delta) };
}
