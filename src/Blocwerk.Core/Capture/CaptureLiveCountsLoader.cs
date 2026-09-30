// <copyright file="CaptureLiveCountsLoader.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Reads <see cref="CaptureLiveCounts"/> for the active model of a wall: the proposal review count
/// (<see cref="ProposalCoverage.ListedCountAsync"/>), one narrow query over the live holds and one over the visible volume ids.
/// </summary>
internal static class CaptureLiveCountsLoader
{
    /// <summary>Whether a capture's record has a step whose stored count <see cref="LoadAsync"/> would replace.</summary>
    /// <param name="record">The record.</param>
    /// <returns>True when live counts are worth reading.</returns>
    public static bool IsWanted(CaptureFollowUpRecord record) =>
        CaptureFollowUpText.ReportsProposals(record)
        || CaptureFollowUpText.ReportsCount(record, PlaceHoldsFollowUpStep.StepKey)
        || CaptureFollowUpText.ReportsCount(record, DetectVolumesFollowUpStep.StepKey);

    /// <summary>The live counts of <paramref name="modelId"/> (the wall's active model).</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="modelId">The active model.</param>
    /// <param name="logger">Where a failure of the proposal coverage check is logged.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts.</returns>
    public static async Task<CaptureLiveCounts> LoadAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, ILogger logger, CancellationToken ct)
    {
        var proposals = await ProposalCoverage.ListedCountAsync(db, wallId, modelId, logger, ct);
        var holds = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking()
            .Where(h => h.MetricSource == HoldMetric.TextureRegistration || h.MetricSource == HoldMetric.TextureRegistrationCarried
                || h.MetricSource == HoldMetric.TextureRegistrationRejected || h.VolumePlacementJson != null)
            .Select(h => new { h.FacetId, h.PlaneAMm, h.PlaneBMm, h.MetricSource, h.VolumePlacementJson })
            .ToListAsync(ct);
        var volumes = (await db.WallVolumes.AsNoTracking()
            .Where(v => v.GeometryModelId == modelId && !v.IsHidden && !v.IsRemoved)
            .Select(v => v.Id)
            .ToListAsync(ct)).ToHashSet();

        var placed = holds.Where(h => h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null
            && h.MetricSource is HoldMetric.TextureRegistration or HoldMetric.TextureRegistrationCarried).ToList();
        var onVolumes = holds.Count(h => HoldVolumePlacement.FromJson(h.VolumePlacementJson) is { } p
            && volumes.Contains(p.VolumeId) && p.Matches(h.PlaneAMm, h.PlaneBMm));
        return new CaptureLiveCounts(
            proposals,
            placed.Count,
            placed.Count(h => h.MetricSource == HoldMetric.TextureRegistrationCarried),
            holds.Count(h => h.MetricSource == HoldMetric.TextureRegistrationRejected),
            volumes.Count,
            onVolumes);
    }
}
