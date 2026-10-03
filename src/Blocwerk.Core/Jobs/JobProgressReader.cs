// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Data;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Jobs;

/// <summary>
/// <see cref="IJobProgressReader"/>: one bounded query per source (captures with their timeline and follow-up record, GPU
/// jobs, the import staging folders) plus one for the stage history, mapped per kind in the partial files beside this one.
/// Reads through the root context (no membership filter): the scope is the authorization.
/// </summary>
public sealed partial class JobProgressReader(
    RootDbContextFactory dbContextFactory,
    CaptureImportProgress? imports = null,
    TimeProvider? clock = null,
    GpuJobQueue? runnerQueue = null) : IJobProgressReader
{
    /// <summary>How many rows of each source with only ended work are read at most.</summary>
    public const int MaxRows = 200;

    /// <summary>A safety cap on the rows with running work (far above what one installation runs at once).</summary>
    public const int MaxActiveRows = 2000;

    /// <summary>How many ended jobs a snapshot lists at most (running ones are always all listed).</summary>
    public const int MaxItems = 300;

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public async Task<JobProgressSnapshot> ReadAsync(JobProgressScope scope, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = now - (scope.Recent < TimeSpan.Zero ? TimeSpan.Zero : scope.Recent);
        await using var db = dbContextFactory.CreateDbContext();
        var history = await JobStageHistory.LoadAsync(db, ct);
        var context = new JobProgressReadContext(scope, since, now, history);

        var items = new List<JobProgressItem>();
        items.AddRange(await CaptureItemsAsync(db, context, ct));
        items.AddRange(await GpuItemsAsync(db, context, ct));
        items.AddRange(await ImportItemsAsync(context, ct));
        items = await MarkPausedAsync(db, items, context, ct);
        var named = await NameWallsAsync(db, items, ct);
        return new JobProgressSnapshot(now, Math.Round(scope.Recent.TotalHours, 2), Order(named));
    }

    /// <summary>Running and queued jobs first (newest start first), then ended ones (newest end first).</summary>
    internal static List<JobProgressItem> Order(IEnumerable<JobProgressItem> items) =>
        items
            .OrderBy(i => JobStates.IsActive(i.State) ? 0 : 1)
            .ThenBy(i => i.State == JobStates.Running ? 0 : 1)
            .ThenByDescending(i => JobStates.IsActive(i.State) ? i.StartedAt ?? i.UpdatedAt : i.EndedAt ?? i.UpdatedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .Where((i, index) => JobStates.IsActive(i.State) || index < MaxItems)
            .ToList();

    private static async Task<List<JobProgressItem>> NameWallsAsync(BlocwerkDbContext db, List<JobProgressItem> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.WallId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return items;
        }

        var names = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .Select(w => new { w.Id, w.Name })
            .ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        return items.Select(i => i with { WallName = names.GetValueOrDefault(i.WallId) }).ToList();
    }

    private async Task<IEnumerable<JobProgressItem>> ImportItemsAsync(JobProgressReadContext context, CancellationToken ct)
    {
        if (imports is null)
        {
            return [];
        }

        var open = await imports.ListAsync(ct);
        return open
            .Where(i => context.Scope.Covers(i.WallId))
            .Select(i => ImportItem(i, JobProgressReadContext.FromRate(i.DoneBytes, i.TotalBytes, 0, i.StartedAt, context.Now)));
    }

    private static JobProgressItem ImportItem(CaptureImportProgressInfo i, (double? Eta, string? Source) eta) =>
        new()
        {
            Id = $"{JobKinds.Import}:{i.ImportId}",
            Kind = JobKinds.Import,
            State = JobStates.Running,
            Stage = "uploading",
            Detail = string.Create(CultureInfo.InvariantCulture, $"{i.DoneBytes:N0} of {i.TotalBytes:N0} bytes uploaded"),
            Percent = i.TotalBytes > 0 ? JobEta.Percent((double)i.DoneBytes / i.TotalBytes) : null,
            EtaSeconds = eta.Eta,
            EtaSource = eta.Source,
            StartedAt = i.StartedAt,
            UpdatedAt = i.UpdatedAt,
            WallId = i.WallId,
            CaptureId = i.ImportId,
        };
}
