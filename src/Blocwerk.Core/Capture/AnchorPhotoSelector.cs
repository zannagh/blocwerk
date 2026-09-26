// <copyright file="AnchorPhotoSelector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Picks the anchor photos of a markerless capture: retained photos of the ACTIVE model's capture whose wall-frame
/// poses that model knows. They are reconstructed together with the new photos, so wall-geometry's <c>solve-sfm</c> can
/// fit the new model into the wall's existing frame. Greedy and deterministic (a resumed capture picks the same):
/// first the camera that sees the most of the wall, then each step the camera that adds the most facet coverage
/// (every sample point wants two anchors) plus the most spread (distance to the nearest anchor already picked).
/// </summary>
public static class AnchorPhotoSelector
{
    /// <summary>How many anchors a capture sends (the solver needs at least 6 that register).</summary>
    public const int DefaultCount = 15;

    /// <summary>Coverage is wanted from this many anchors per sample point.</summary>
    private const int WantedViews = 2;

    /// <summary>One newly covered sample point is worth this much spread, mm.</summary>
    private const double CoverageWeightMm = 1000;

    /// <summary>Sample points per facet side (a 3 × 3 grid over its extent).</summary>
    private const int SamplesPerSide = 3;

    /// <summary>The camera images (<c>p01</c>, …) to send as anchors, in selection order.</summary>
    /// <param name="referenceJson">The active model's JSON (its cameras and facets).</param>
    /// <param name="available">The camera images whose photo is still stored.</param>
    /// <param name="count">How many to pick at most.</param>
    /// <returns>The picked images; empty when the model has no usable camera for them.</returns>
    public static IReadOnlyList<string> Select(string referenceJson, IReadOnlySet<string> available, int count = DefaultCount)
    {
        var cameras = CoverageCamera.FromModel(referenceJson)
            .Where(c => available.Contains(c.Camera.Image))
            .OrderBy(c => c.Camera.Image, StringComparer.Ordinal)
            .ToList();
        var samples = Samples(WallGeometryDocument.Parse(referenceJson));
        var sees = cameras.Select(c => samples.Select((p, i) => (p, i)).Where(s => c.InFrame(s.p)).Select(s => s.i).ToHashSet()).ToList();
        var views = new int[samples.Count];
        var picked = new List<int>();
        while (picked.Count < Math.Min(count, cameras.Count))
        {
            var best = -1;
            var bestScore = double.NegativeInfinity;
            for (var k = 0; k < cameras.Count; k++)
            {
                if (picked.Contains(k))
                {
                    continue;
                }

                var score = Score(k, cameras, sees, views, picked);
                if (score > bestScore)
                {
                    (best, bestScore) = (k, score);
                }
            }

            picked.Add(best);
            foreach (var i in sees[best])
            {
                views[i]++;
            }
        }

        return picked.Select(k => cameras[k].Camera.Image).ToList();
    }

    private static double Score(int k, List<CoverageCamera> cameras, List<HashSet<int>> sees, int[] views, List<int> picked)
    {
        var gain = sees[k].Count(i => views[i] < WantedViews);
        if (picked.Count == 0)
        {
            return sees[k].Count;
        }

        var spread = picked.Min(p => Vec3.Distance(cameras[k].Centre, cameras[p].Centre));
        return (gain * CoverageWeightMm) + spread;
    }

    /// <summary>A 3 × 3 grid of points over every facet's extent, world mm.</summary>
    private static List<double[]> Samples(WallGeometryDocument document)
    {
        var points = new List<double[]>();
        foreach (var facet in document.Segments.SelectMany(s => s.Facets))
        {
            if (FacetFrame.From(facet) is not { } frame || facet.ExtentMm is not { Area: > 0 } e)
            {
                continue;
            }

            for (var i = 0; i < SamplesPerSide; i++)
            {
                for (var j = 0; j < SamplesPerSide; j++)
                {
                    var a = e.AMin + (e.Width * (i + 0.5) / SamplesPerSide);
                    var b = e.BMin + (e.Height * (j + 0.5) / SamplesPerSide);
                    points.Add(frame.ToWorld(a, b));
                }
            }
        }

        return points;
    }
}
