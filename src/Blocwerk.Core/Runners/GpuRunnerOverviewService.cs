// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Runners;

/// <summary>
/// <see cref="IGpuRunnerOverviewService"/>: the viewer's scope (site admin, or the walls they administer, decided like the
/// administration area and <see cref="WallAdminGuard"/>), the runners in it, their held jobs (progress from
/// <see cref="IJobProgressReader"/>) and failures, composed by <see cref="GpuRunnerOverviewComposer"/>.
/// </summary>
public sealed partial class GpuRunnerOverviewService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUser,
    GpuJobQueue queue,
    IJobProgressReader progress,
    IKioskContext? kioskContext = null,
    TimeProvider? clock = null) : IGpuRunnerOverviewService
{
    /// <summary>How many recent failures of the runners in view are read for the failure counts.</summary>
    public const int FailureSamples = 300;

    private const string Action = "Watching 3D runners";

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public async Task<GpuRunnerOverview> GetAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var viewer = await ViewerAsync(ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        // Authorized by the viewer's scope from here on; wall names must not vanish behind the membership filter.
        db.CurrentUserId = Guid.Empty;
        var runners = await RunnersAsync(db, viewer, ct);
        var ids = runners.Select(r => r.Runner.Id).ToList();
        var held = await HeldJobsAsync(db, ids, ct);
        var visible = runners
            .Select(r => GpuRunnerOverviewComposer.Held(held, r.Runner.Id) is { } j && viewer.MaySeeJob(j.WallId, r.Runner.OwnerUserId) ? j : null)
            .OfType<GpuRunnerHeldJob>()
            .ToList();
        var jobs = await ProgressAsync(visible, ct);
        var failed = await FailedJobsAsync(db, ids, now, ct);
        var rows = GpuRunnerOverviewComposer.Compose(viewer, runners, held, jobs, failed, now - queue.Options.OnlineWindow);
        return new GpuRunnerOverview(now, viewer.IsAppAdmin, rows);
    }

    /// <summary>The acting user's scope; a kiosk is refused.</summary>
    internal async Task<GpuRunnerViewer> ViewerAsync(CancellationToken ct)
    {
        var user = await currentUser.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, Action);
        if (await AppAdminGuard.IsAppAdminAsync(db, user.Id, ct))
        {
            return new GpuRunnerViewer(user.Id, true, new HashSet<Guid>());
        }

        return new GpuRunnerViewer(user.Id, false, await JobProgressService.AdministeredWallsAsync(db, user.Id, ct));
    }

    /// <summary>The progress items of the held jobs the viewer may see, by job id.</summary>
    private async Task<Dictionary<Guid, JobProgressItem>> ProgressAsync(List<GpuRunnerHeldJob> visible, CancellationToken ct)
    {
        if (visible.Count == 0)
        {
            return [];
        }

        var snapshot = await progress.ReadAsync(new JobProgressScope(visible.Select(j => j.WallId).ToHashSet(), TimeSpan.Zero), ct);
        var ids = visible.Select(j => j.JobId).ToHashSet();
        return snapshot.Jobs
            .Where(j => j.Kind == JobKinds.GpuTraining && j.GpuJobId is { } id && ids.Contains(id))
            .ToDictionary(j => j.GpuJobId!.Value);
    }
}
