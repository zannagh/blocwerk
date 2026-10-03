// <copyright file="CoverageVolumesFingerprint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Identifies the visible volumes a coverage report is computed from: a re-detection, hiding, removing, restoring or
/// switching flat sides changes it, so a stored report drawn with other volumes is stale.
/// </summary>
internal static class CoverageVolumesFingerprint
{
    /// <summary>The fingerprint of a model's visible volumes.</summary>
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
        var text = string.Join('\n', volumes.Select(v => $"{v.FacetId}\t{v.Index}\t{v.SurfaceJson}").Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
