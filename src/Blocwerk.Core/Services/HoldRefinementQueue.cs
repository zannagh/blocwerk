// <copyright file="HoldRefinementQueue.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Threading.Channels;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The in-process <see cref="IHoldRefinementQueue"/>: a channel drained by one background loop that waits
/// <see cref="Debounce"/> after the last edit of a burst, then per wall re-places / re-sizes the holds that
/// still need it (<see cref="HoldGlyphRefresher"/>), lets the 3D model follow them (registration and volume placement)
/// and refines their footprint and protrusion.
/// Single-instance, best-effort: a restart drops the queue and the 3D view keeps projecting the outline.
/// </summary>
public sealed class HoldRefinementQueue(IServiceScopeFactory scopes, ILogger<HoldRefinementQueue> logger)
    : BackgroundService, IHoldRefinementQueue
{
    /// <summary>Quiet time after the last queued edit before a run starts.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(20);

    private readonly Channel<(Guid WallId, Guid[] HoldIds)> channel =
        Channel.CreateUnbounded<(Guid WallId, Guid[] HoldIds)>(new UnboundedChannelOptions { SingleReader = true });

    /// <inheritdoc />
    public void Enqueue(Guid wallId, IEnumerable<Guid> holdIds)
    {
        var ids = holdIds.Distinct().ToArray();
        if (ids.Length > 0)
        {
            channel.Writer.TryWrite((wallId, ids));
        }
    }

    /// <summary>Runs one wall's refinement now (the loop's body; public for tests and admin tools).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="holdIds">The holds.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(Guid wallId, IReadOnlyCollection<Guid> holdIds, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BlocwerkDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var holds = await db.Holds.Where(h => h.WallId == wallId && holdIds.Contains(h.Id)).ToListAsync(ct);
            if (await HoldGlyphRefresher.RefreshAsync(db, holds, logger, ct) > 0)
            {
                await db.SaveChangesAsync(ct);
            }
        }

        await FollowIn3DAsync(scope.ServiceProvider, factory, wallId, holdIds, ct);

        var footprints = scope.ServiceProvider.GetService<IHoldFootprintService>();
        if (footprints is not null)
        {
            await footprints.RefineHoldsFromPipelineAsync(wallId, holdIds, ct);
        }

        var protrusion = scope.ServiceProvider.GetService<IHoldProtrusionService>();
        if (protrusion is not null)
        {
            await protrusion.MeasureHoldsFromPipelineAsync(wallId, holdIds, ct);
        }
    }

    /// <summary>
    /// The 3D model follows the edited holds (their panel position is the truth): placed again from their panel photo's
    /// registration, and placed again on a volume when their stored volume point no longer belongs to their position.
    /// Silent: without a model, textures or volumes nothing happens, and a failure is only logged (the holds keep the
    /// estimate the edit gave them).
    /// </summary>
    private async Task FollowIn3DAsync(
        IServiceProvider services, IDbContextFactory<BlocwerkDbContext> factory, Guid wallId, IReadOnlyCollection<Guid> holdIds, CancellationToken ct)
    {
        try
        {
            if (services.GetService<IHoldTexturePlacementService>() is { } placement)
            {
                await placement.PlaceEditedAsync(wallId, holdIds, ct);
            }

            await using var db = await factory.CreateDbContextAsync(ct);
            await PlaceOnVolumesAsync(db, wallId, holdIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Following {Count} edited holds in 3D on wall {WallId} failed; they keep their estimated placement", holdIds.Count, wallId);
        }
    }

    /// <summary>
    /// The edited holds whose volume placement is missing or stale, placed again on the active model's visible volumes; the
    /// replaced placements are recorded in the model's rolling edit run, so reverting it restores them too.
    /// </summary>
    private static async Task PlaceOnVolumesAsync(BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid> holdIds, CancellationToken ct)
    {
        var modelId = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        if (modelId is not { } active || !await db.WallVolumes.AnyAsync(v => v.GeometryModelId == active && !v.IsHidden && !v.IsRemoved, ct))
        {
            return;
        }

        var holds = await db.Holds.AsNoTracking().Where(h => h.WallId == wallId && holdIds.Contains(h.Id))
            .Select(h => new { h.Id, h.PlaneAMm, h.PlaneBMm, h.VolumePlacementJson })
            .ToListAsync(ct);
        var stale = holds
            .Where(h => HoldVolumePlacement.FromJson(h.VolumePlacementJson) is not { } p || !p.Matches(h.PlaneAMm, h.PlaneBMm))
            .ToDictionary(h => h.Id, h => h.VolumePlacementJson);
        if (stale.Count == 0)
        {
            return;
        }

        var changed = new List<Guid>();
        await WallVolumeService.PlaceHoldsAsync(db, wallId, active, changed, ct, stale.Keys.ToHashSet());
        await HoldTexturePlacementService.RecordVolumePlacementsAsync(db, wallId, active, changed.ToDictionary(id => id, id => stale[id]), ct);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new Dictionary<Guid, HashSet<Guid>>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = await channel.Reader.ReadAsync(stoppingToken);
                Add(pending, item);
                await DrainBurstAsync(pending, stoppingToken);
                foreach (var (wallId, ids) in pending)
                {
                    await RunAsync(wallId, ids, stoppingToken);
                }

                pending.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refining edited holds failed; the 3D view keeps projecting their outlines");
                pending.Clear();
            }
        }
    }

    private static void Add(Dictionary<Guid, HashSet<Guid>> pending, (Guid WallId, Guid[] HoldIds) item)
    {
        if (!pending.TryGetValue(item.WallId, out var set))
        {
            pending[item.WallId] = set = [];
        }

        set.UnionWith(item.HoldIds);
    }

    /// <summary>Keeps folding edits in until nothing arrives for <see cref="Debounce"/>.</summary>
    private async Task DrainBurstAsync(Dictionary<Guid, HashSet<Guid>> pending, CancellationToken ct)
    {
        while (true)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(Debounce);
            try
            {
                Add(pending, await channel.Reader.ReadAsync(quiet.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
