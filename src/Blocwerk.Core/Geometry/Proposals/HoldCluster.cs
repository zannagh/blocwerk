// <copyright file="HoldCluster.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>A physical hold seen by several photos: its detections (one per photo) and its triangulated point.</summary>
/// <param name="Hits">The detections' wall hits, at most one per photo.</param>
/// <param name="Point">The point nearest all their rays (world mm), or the mean hit when the rays are nearly parallel.</param>
/// <param name="ResidualMm">Median distance of the point to the rays, mm.</param>
public sealed record HoldCluster(IReadOnlyList<SurfaceHit> Hits, double[] Point, double ResidualMm)
{
    /// <summary>Photos that see it.</summary>
    public int Views => Hits.Count;
}
