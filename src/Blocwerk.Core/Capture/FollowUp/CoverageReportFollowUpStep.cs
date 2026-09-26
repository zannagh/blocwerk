// <copyright file="CoverageReportFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 6 (after the capture completed): what the capture saw of the wall and what the next capture should add
/// (<see cref="ICaptureCoverageService.ComputeFromPipelineAsync"/>): weak areas per facet, volume faces nobody shot,
/// facets short of markers, video passes missing from the recipe. CPU only and seconds; it only writes the report.
/// Runs once the capture is done (the volumes and the photo-real frame are known then), and again when the visible
/// volumes or the photo-real view changed.
/// </summary>
public sealed class CoverageReportFollowUpStep(ICaptureCoverageService coverage, IDbContextFactory<BlocwerkDbContext> dbContextFactory)
    : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "coverage-report";

    /// <inheritdoc />
    public int Order => 500;

    /// <inheritdoc />
    public string Title => "Checking what the capture covered";

    /// <inheritdoc />
    public bool NeedsPhotoReal => false;

    /// <inheritdoc />
    public bool RunsAfterCompletion => true;

    /// <inheritdoc />
    public bool KeptByCorrection => false;

    /// <inheritdoc />
    public async Task<string?> InputsKeyAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var volumes = await db.WallVolumes.AsNoTracking()
            .Where(v => v.GeometryModelId == context.ModelId && !v.IsHidden)
            .Select(v => new { v.FacetId, v.Index, v.SurfaceJson })
            .ToListAsync(ct);
        var text = string.Join('\n', volumes.Select(v => $"{v.FacetId}\t{v.Index}\t{v.SurfaceJson}").Order(StringComparer.Ordinal));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        return $"coverage:{context.SplatId?.ToString("N") ?? "none"}:{hash}";
    }

    /// <inheritdoc />
    public async Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        var report = await coverage.ComputeFromPipelineAsync(context.CaptureId, ct);
        return report is null
            ? CaptureFollowUpStepResult.Skipped("the capture has no model to check")
            : CaptureFollowUpStepResult.Done(Describe(report.Advice.Count));
    }

    /// <summary>"4 tips for the next capture" (empty when there are none).</summary>
    internal static string Describe(int tips) =>
        tips <= 0 ? string.Empty : $"{CaptureFollowUpText.Count(tips, "tip", "tips")} for the next capture";
}
