// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The re-solve and re-render workers' idle rescan: a worker that waited <see cref="WallCapturePipelineOptions.RedoRescanInterval"/>
/// without work queues again every capture still carrying its mark that no run of this process is working on (a run that
/// failed before it could clear the mark), so the wall does not stay busy until a restart.
/// </summary>
internal static class CaptureRedoRescan
{
    /// <summary>The next queued capture, or null after <paramref name="idle"/> without one. Shutdown throws.</summary>
    public static async Task<Guid?> NextAsync(Func<CancellationToken, ValueTask<Guid>> dequeue, TimeSpan idle, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(idle);
        try
        {
            return await dequeue(timeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The captures marked for <paramref name="kind"/> that no run of <paramref name="processor"/> is working on and that
    /// may run again (a mark whose runs keep not ending is dropped here, <see cref="WallCaptureProcessor.MayQueueRedoAsync"/>).
    /// </summary>
    public static async Task<List<Guid>> StuckAsync(
        RootDbContextFactory dbContextFactory, CaptureRedoKind kind, WallCaptureProcessor processor, CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            var marked = kind == CaptureRedoKind.Resolve
                ? db.WallCaptures.Where(c => c.SolveJobId != null && c.SolveJobId.StartsWith(CaptureResolveMark.Mark))
                : db.WallCaptures.Where(c => c.TexturesJobId != null && c.TexturesJobId.StartsWith(CaptureTextureOutcome.RerenderMark));
            ids = await marked.Select(c => c.Id).ToListAsync(ct);
        }

        var stuck = new List<Guid>();
        foreach (var id in ids.Where(id => !processor.IsRedoing(id)))
        {
            if (await processor.MayQueueRedoAsync(id, kind, ct))
            {
                stuck.Add(id);
            }
        }

        return stuck;
    }
}
