// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Which photo-real qualities a wall may pick. Draft, High and Max always; <see cref="SplatQuality.Ultra"/> only when
/// something can really train it: a 3D runner that may serve the wall reports it (gsplat on a 12 GB CUDA GPU), or the
/// splat worker says so itself (<see cref="ComputeHealth.MaxQuality"/>, asked at most every five minutes).
/// </summary>
public sealed class SplatQualityOffer(
    GpuJobQueue queue,
    IComputeJobClientFactory clients,
    ILogger<SplatQualityOffer> logger,
    TimeProvider? clock = null)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim probe = new(1, 1);
    private (DateTimeOffset At, bool Ultra)? worker;

    /// <summary>Whether <see cref="SplatQuality.Ultra"/> is on offer for this wall.</summary>
    public async Task<bool> UltraAvailableAsync(Guid wallId, CancellationToken ct)
    {
        if (queue.Options.Mode != GpuRunnerMode.Off && await queue.UltraAvailableForWallAsync(wallId, ct))
        {
            return true;
        }

        return await WorkerTrainsUltraAsync(ct);
    }

    /// <summary>Whether the splat worker trains ultra itself (cached; false when it cannot be asked).</summary>
    public async Task<bool> WorkerTrainsUltraAsync(CancellationToken ct)
    {
        var client = clients.Get(ComputeServiceKind.Splat);
        if (!client.IsConfigured)
        {
            return false;
        }

        await probe.WaitAsync(ct);
        try
        {
            if (worker is { } cached && time.GetUtcNow() - cached.At < CacheFor)
            {
                return cached.Ultra;
            }

            var ultra = false;
            try
            {
                ultra = CaptureSplatDocuments.ParseQuality((await client.GetHealthAsync(ct)).MaxQuality) == SplatQuality.Ultra;
            }
            catch (ComputeJobException ex)
            {
                logger.LogDebug("Could not ask the splat worker for its qualities: {Reason}", ex.Message);
            }

            worker = (time.GetUtcNow(), ultra);
            return ultra;
        }
        finally
        {
            probe.Release();
        }
    }
}
