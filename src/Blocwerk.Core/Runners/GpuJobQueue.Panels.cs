// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Panel registration jobs (<see cref="GpuJobKind.RegisterPanels"/>): a wall's panel photos registered into its capture's
/// cameras on a 3D runner. Nothing waits on them; a delivered result is marked installed at once and its poses are read
/// when holds are placed on the surface (<c>HoldTexturePlacementService.Surface</c>).
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>The wall's latest panel registration job for <paramref name="modelId"/> (any state), or null.</summary>
    public async Task<GpuJob?> LatestPanelsJobAsync(Guid wallId, Guid modelId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.GpuJobs.AsNoTracking()
            .Where(j => j.WallId == wallId && j.GeometryModelId == modelId && j.Kind == GpuJobKind.RegisterPanels)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The poses of a succeeded panel registration job, or null when its result is gone or unreadable.</summary>
    public IReadOnlyDictionary<Guid, PanelPose>? PanelPoses(GpuJob job)
    {
        if (job is not { Kind: GpuJobKind.RegisterPanels, Status: GpuJobStatus.Succeeded, ResultPath: { } stored }
            || files.ResolvePhysicalPath(stored) is not { } path)
        {
            return null;
        }

        try
        {
            return RunnerPanelPoses.Read(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            logger.LogWarning("GPU job {JobId}: the panel poses cannot be read: {Reason}", job.Id, ex.Message);
            return null;
        }
    }

    private string? ValidateStoredPanels(string stored, GpuRunner runner, Guid jobId)
    {
        try
        {
            var path = files.ResolvePhysicalPath(stored) ?? throw new InvalidDataException("The upload was not stored.");
            _ = RunnerPanelPoses.Read(path);
            return RunnerPanelPoses.Format;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            logger.LogWarning("Runner {RunnerId} sent an invalid panel registration result for GPU job {JobId}: {Reason}", runner.Id, jobId, ex.Message);
            return null;
        }
    }
}
