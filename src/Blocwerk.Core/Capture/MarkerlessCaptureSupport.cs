// <copyright file="MarkerlessCaptureSupport.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Compute;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Whether this server can measure a wall WITHOUT markers: the geometry service offers <c>solve-sfm</c> and the splat
/// worker's <c>splat-prepare</c> returns <c>sparse.zip</c> and takes anchor photos (its <c>/health</c>
/// <c>prepareOutputs</c>). Anything missing or unreachable = no: captures then behave exactly as before.
/// </summary>
public static class MarkerlessCaptureSupport
{
    /// <summary>The wall-geometry kind that solves a feature reconstruction.</summary>
    public const string SolveKind = "solve-sfm";

    /// <summary>The splat worker's prepare output the feature solve reads.</summary>
    public const string SparseOutput = "sparse.zip";

    /// <summary>The splat worker's prepare feature for anchor photos.</summary>
    public const string AnchorsOutput = "anchors";

    /// <summary>Asks both workers (their unauthenticated <c>/health</c>). Never throws for a worker problem.</summary>
    /// <param name="clients">The configured workers.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when a markerless capture can run.</returns>
    public static async Task<bool> IsAvailableAsync(IComputeJobClientFactory clients, CancellationToken ct)
    {
        var geometry = clients.Get(ComputeServiceKind.Geometry);
        var splat = clients.Get(ComputeServiceKind.Splat);
        if (!geometry.IsConfigured || !splat.IsConfigured)
        {
            return false;
        }

        try
        {
            var solver = await geometry.GetHealthAsync(ct);
            if (!solver.Kinds.Contains(SolveKind))
            {
                return false;
            }

            var worker = await splat.GetHealthAsync(ct);
            return worker.Kinds.Contains(WallCaptureProcessor.PrepareKind)
                   && worker.PrepareOutputs.Contains(SparseOutput)
                   && worker.PrepareOutputs.Contains(AnchorsOutput);
        }
        catch (ComputeJobException)
        {
            return false;
        }
    }
}
