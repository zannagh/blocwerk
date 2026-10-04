// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Where a texture re-render runs: on the host's geometry worker when the quality fits its memory budget, else (when one is
/// online) on a 3D runner with more memory (<see cref="TextureRoutes"/>). The runner's budget is the
/// <c>texturesMemoryMb</c> it advertised; the job needs the quality's full blend (<see cref="TextureQualityEstimate.RequiredMb"/>).
/// </summary>
public sealed partial class WallCaptureService
{
    public async Task<TextureRouting> GetTextureRoutingAsync(Guid captureId)
    {
        var (db, _, capture) = await OpenCaptureAsync(captureId);
        await using (db)
        {
            if (await TextureInputsAsync(db, capture) is not { } inputs)
            {
                return TextureRouting.None;
            }

            var configured = settings?.GeometryTextures ?? new GeometryTextureSettings();
            var host = TextureQualityEstimate.ForAll(inputs.Geometry, inputs.Photos, configured);
            if (gpuJobs is null || gpuJobs.Options.Mode == Runners.GpuRunnerMode.Off)
            {
                return TextureRoutes.Decide(host, _ => null, false);
            }

            var online = (await gpuJobs.TexturesRunnersAsync(capture.WallId, 0, CancellationToken.None)).Count > 0;
            var queue = await gpuJobs.TexturesQueueAsync(CancellationToken.None);
            var offers = new Dictionary<TextureQuality, TextureRunnerOffer?>();
            foreach (var estimate in host.Where(e => e.Fit != TextureBlendFit.Full))
            {
                var needed = TextureQualityEstimate.RequiredMb(inputs.Geometry, estimate.Quality, inputs.Photos, configured);
                var runner = (await gpuJobs.TexturesRunnersAsync(capture.WallId, needed, CancellationToken.None)).FirstOrDefault();
                offers[estimate.Quality] = runner is null
                    ? null
                    : new TextureRunnerOffer(runner.Name, runner.Paused, runner.Busy, queue.Waiting, queue.Running);
            }

            return TextureRoutes.Decide(host, q => offers.GetValueOrDefault(q), online);
        }
    }

    /// <summary>Why a runner cannot render this quality now (no capable runner online), or null.</summary>
    private async Task<string?> RunnerProblemAsync(Data.BlocwerkDbContext db, WallCapture capture, TextureQuality quality)
    {
        if (gpuJobs is null || gpuJobs.Options.Mode == Runners.GpuRunnerMode.Off)
        {
            return "3D runners are switched off on this server.";
        }

        if (await TextureInputsAsync(db, capture) is not { } inputs)
        {
            return "This capture's 3D model is not readable.";
        }

        var needed = TextureQualityEstimate.RequiredMb(inputs.Geometry, quality, inputs.Photos, settings?.GeometryTextures ?? new GeometryTextureSettings());
        return (await gpuJobs.TexturesRunnersAsync(capture.WallId, needed, CancellationToken.None)).Count == 0
            ? "No online 3D runner that renders textures has enough memory for this quality."
            : null;
    }
}
