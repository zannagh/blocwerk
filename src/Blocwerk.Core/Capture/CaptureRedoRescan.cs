// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
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

    /// <summary>The marked captures (<paramref name="marked"/>) no run of <paramref name="processor"/> is working on.</summary>
    public static async Task<List<Guid>> StuckAsync(
        RootDbContextFactory dbContextFactory,
        Func<IQueryable<WallCapture>, IQueryable<WallCapture>> marked,
        WallCaptureProcessor processor,
        CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var ids = await marked(db.WallCaptures).Select(c => c.Id).ToListAsync(ct);
        return ids.Where(id => !processor.IsRedoing(id)).ToList();
    }
}
