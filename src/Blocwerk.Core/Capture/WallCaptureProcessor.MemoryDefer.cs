// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A geometry job that needed more memory than the host's worker allows one job (<see cref="CaptureNeedsMoreMemoryException"/>)
/// is not a failure: the work waits for a machine that has it. Textures go to a 3D runner through the re-render path
/// (<see cref="CaptureTextureOutcome.RerenderOnRunner"/>; the re-render worker queues the GPU job once the capture's own run
/// has left <see cref="WallCaptureStatus.Texturing"/>). A solve has no runner kind yet: with runners on, the capture stays in
/// <see cref="WallCaptureStatus.Solving"/> with the reason as its stage and is solved again on the next attempt (the capture
/// worker resumes every unfinished capture on start), for at most <see cref="SolveParkLifetime"/> after it started; then, or
/// with runners off, it fails with the reason (its photos stay, as for any failed capture).
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>The model is active; its textures wait for a 3D runner.</summary>
    internal const string TexturesDeferredText =
        "The 3D model is active; its textures need more memory than this server has and " + CaptureNeedsMoreMemoryException.WaitingText + ".";

    /// <summary>How long a solve that needs more memory than this server has may keep waiting for another attempt.</summary>
    internal static readonly TimeSpan SolveParkLifetime = TimeSpan.FromHours(24);

    private bool RunnersOn => gpuJobs is not null && gpuJobs.Options.Mode != GpuRunnerMode.Off;

    /// <summary>Marks the capture's textures for a 3D runner and returns the texture outcome the capture shows meanwhile.</summary>
    private async Task<string> DeferTexturesToRunnerAsync(Guid captureId, TextureQuality quality, CancellationToken ct)
    {
        await UpdateAsync(captureId, c => c.TexturesJobId = CaptureTextureOutcome.Mark(quality, null, onRunner: true), ct);
        await UpdateRecordAsync(captureId, r => r with { RerenderStarts = 0 }, ct);
        logger.LogInformation("Textures of capture {CaptureId} ({Quality}) need more memory than this server has: deferred to a 3D runner", captureId, quality);
        return TexturesDeferredText;
    }

    /// <summary>
    /// A solving capture (runners on, within <see cref="SolveParkLifetime"/>) waits in <see cref="WallCaptureStatus.Solving"/>
    /// and the next attempt submits it again; anything else that ran out of memory fails with the reason.
    /// </summary>
    private async Task ParkSolveOrFailAsync(Guid captureId, string reason, CancellationToken ct)
    {
        DateTimeOffset? started;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            started = await db.WallCaptures.Where(c => c.Id == captureId && c.Status == WallCaptureStatus.Solving)
                .Select(c => c.StartedAt ?? DateTimeOffset.UtcNow).Cast<DateTimeOffset?>().FirstOrDefaultAsync(ct);
        }

        if (started is null || !RunnersOn)
        {
            await FailAsync(captureId, $"{reason}. The photos are kept; a capture with fewer photos fits.", ct);
            return;
        }

        if (DateTimeOffset.UtcNow - started.Value > SolveParkLifetime)
        {
            await FailAsync(captureId, $"{reason}, also after retrying for {SolveParkLifetime.TotalHours:0} hours. The photos are kept; a capture with fewer photos fits.", ct);
            return;
        }

        logger.LogInformation("Capture {CaptureId}: {Reason}; it waits for the next attempt", captureId, reason);
        await UpdateAsync(
            captureId,
            c =>
            {
                c.Status = WallCaptureStatus.Solving;
                c.Stage = $"{reason}: {CaptureNeedsMoreMemoryException.RetryText}";
                c.SolveJobId = null;

                // A reconstruction the kernel stopped is redone too, not polled again.
                c.SfmJobId = null;

                // Waiting is not a failed attempt.
                c.Attempts = 0;
            },
            ct);
    }
}
