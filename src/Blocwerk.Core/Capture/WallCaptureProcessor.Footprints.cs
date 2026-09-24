// <copyright file="WallCaptureProcessor.Footprints.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Capture;

/// <summary>
/// After a solve: the capture's photos and solved cameras are exactly what the multi-view hold
/// footprints need, so they are refined here, once, instead of on any page view. After the splat: the
/// scene's surface gives each hold's protrusion (how far the 3D view lifts its overlay).
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task RefineFootprintsAsync(CaptureRun run, CancellationToken ct)
    {
        if (scopes is null)
        {
            return;
        }

        await UpdateAsync(run.Capture.Id, c => c.Stage = "Refining 3D hold shapes", ct);
        await using var scope = scopes.CreateAsyncScope();
        var footprints = scope.ServiceProvider.GetService<IHoldFootprintService>();
        if (footprints is not null)
        {
            await footprints.RefineFromPipelineAsync(run.Capture.WallId, ct);
        }
    }

    private async Task MeasureProtrusionAsync(Guid captureId, Guid wallId, CancellationToken ct)
    {
        if (scopes is null)
        {
            return;
        }

        await UpdateAsync(captureId, c => c.Stage = "Photo-real view: measuring the holds", ct);
        await using var scope = scopes.CreateAsyncScope();
        var protrusion = scope.ServiceProvider.GetService<IHoldProtrusionService>();
        if (protrusion is not null)
        {
            await protrusion.MeasureFromPipelineAsync(wallId, ct);
        }
    }
}
