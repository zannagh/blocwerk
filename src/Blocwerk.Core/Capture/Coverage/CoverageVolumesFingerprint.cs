// <copyright file="CoverageVolumesFingerprint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Identifies the visible volumes and placed-hold bounds a coverage report is computed from: a re-detection, hiding,
/// removing, restoring or switching flat sides changes it, as does a hold edit that moves a facet's hold bounds (they
/// widen its region, <see cref="CoverageHoldBounds"/>), so a stored report drawn from other inputs is stale.
/// </summary>
internal static class CoverageVolumesFingerprint
{
    /// <summary>The fingerprint of a model's visible volumes and placed-hold bounds.</summary>
    /// <param name="db">The context.</param>
    /// <param name="modelId">The model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A short hex hash.</returns>
    public static async Task<string> ComputeAsync(BlocwerkDbContext db, Guid modelId, CancellationToken ct)
    {
        var volumes = await db.WallVolumes.AsNoTracking()
            .Where(v => v.GeometryModelId == modelId && !v.IsHidden && !v.IsRemoved)
            .Select(v => new { v.FacetId, v.Index, v.SurfaceJson })
            .ToListAsync(ct);
        var holds = CoverageHoldBounds.Lines(await CoverageHoldBounds.LoadAsync(db, modelId, ct));
        var text = string.Join('\n', volumes.Select(v => $"{v.FacetId}\t{v.Index}\t{v.SurfaceJson}").Order(StringComparer.Ordinal).Concat(holds));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
