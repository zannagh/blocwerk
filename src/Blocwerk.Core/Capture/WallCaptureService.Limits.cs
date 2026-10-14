// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The size check every start and redo of a capture runs before it queues anything (<see cref="CaptureLimits"/>): the
/// capture's photos and video frames against this server's capture limits and against what the workers report they take per
/// job. A worker that cannot be asked (down, an older version) is not held against the capture: the job itself still refuses.
/// </summary>
public sealed partial class WallCaptureService
{
    /// <summary>How long the check waits for a worker's limits.</summary>
    private static readonly TimeSpan LimitsProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Why the capture will not fit (shown as a problem; nothing is queued), or null.</summary>
    /// <param name="db">An open context.</param>
    /// <param name="captureId">The capture.</param>
    /// <param name="geometry">Whether the 3D model service gets the images.</param>
    /// <param name="splat">Whether the photo-real worker gets them.</param>
    /// <returns>The problem, or null.</returns>
    internal async Task<string?> FitProblemAsync(BlocwerkDbContext db, Guid captureId, bool geometry, bool splat)
    {
        var photos = await db.WallCapturePhotos.CountAsync(p => p.CaptureId == captureId);
        var video = await db.WallCaptures.Where(c => c.Id == captureId)
            .Select(c => new { c.VideoStoredPath, c.VideoFramesJson }).FirstOrDefaultAsync();

        // Frames not taken yet (a capture that starts): as many as the extraction may take.
        var frames = video?.VideoFramesJson is { } json ? CaptureVideoFiles.Frames(json).Count
            : video?.VideoStoredPath is not null ? PipelineOptions.MaxVideoFrames : 0;
        var geometryCap = geometry ? await MaxImagesAsync(ComputeServiceKind.Geometry) : null;
        var splatCap = splat ? await MaxImagesAsync(ComputeServiceKind.Splat) : null;
        return CaptureLimits.Refusal(photos, frames, PipelineOptions, geometryCap, splatCap);
    }

    private async Task<int?> MaxImagesAsync(ComputeServiceKind kind)
    {
        var client = computeClients.Get(kind);
        if (!client.IsConfigured)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(LimitsProbeTimeout);
        try
        {
            return await client.GetMaxImagesAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is ComputeJobException or OperationCanceledException)
        {
            return null;
        }
    }
}
