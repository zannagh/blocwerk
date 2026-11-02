// <copyright file="ModelMarkerProjection.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Services;

/// <summary>A printed marker of the active model: its facet and its four corners on that facet's plane, mm.</summary>
/// <param name="FacetId">The facet it is mounted on.</param>
/// <param name="CornersMm">Its corners (plane a, b), mm.</param>
public sealed record ModelMarker(string FacetId, IReadOnlyList<(double A, double B)> CornersMm);

/// <summary>
/// Where the active model's printed markers lie on a staged panel photo: each marker's plane corners mapped through the
/// carried holds of its facet (their plane spot on the model against their spot on the staged photo, fitted locally,
/// <see cref="LocalAffine"/>). A sheet the panel photo does not decode (too dark, too oblique, glare) is still a sheet,
/// and every marker of the model counts, a retired id too: only its use for the geometry is retired, not the paper.
/// </summary>
public static class ModelMarkerProjection
{
    /// <summary>Fewest carried holds on a facet for its markers to be mapped.</summary>
    public const int MinimumAnchors = 6;

    /// <summary>The model's physically observed markers (synthetic ones are skipped); empty when the JSON does not parse.</summary>
    /// <param name="modelJson">The geometry model's JSON.</param>
    /// <returns>The markers.</returns>
    public static IReadOnlyList<ModelMarker> Parse(string modelJson)
    {
        try
        {
            return (JsonNode.Parse(modelJson)?["markers"] as JsonArray ?? [])
                .Select(Marker)
                .OfType<ModelMarker>()
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The markers' quads on the staged photo whose centre lies on it, RAW pixels.</summary>
    /// <param name="markers">The model's markers.</param>
    /// <param name="anchors">The carried holds with their plane spot (photo X/Y normalized, as <see cref="PlaneAnchor"/>).</param>
    /// <param name="size">The staged photo's RAW size.</param>
    /// <returns>The projected quads.</returns>
    public static List<IReadOnlyList<(double X, double Y)>> Project(
        IReadOnlyList<ModelMarker> markers, IReadOnlyList<PlaneAnchor> anchors, (int Width, int Height) size)
    {
        var quads = new List<IReadOnlyList<(double X, double Y)>>();
        var byFacet = anchors
            .GroupBy(a => a.FacetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(a => new PointPair(a.A, a.B, a.X * size.Width, a.Y * size.Height)).ToList(), StringComparer.Ordinal);
        foreach (var marker in markers)
        {
            if (!byFacet.TryGetValue(marker.FacetId, out var pairs) || pairs.Count < MinimumAnchors)
            {
                continue;
            }

            var corners = marker.CornersMm.Select(c => LocalAffine.Predict(pairs, c.A, c.B)).ToList();
            if (corners.Any(c => c is null))
            {
                continue;
            }

            var quad = corners.Select(c => c!.Value).ToList();
            var (cx, cy) = (quad.Average(c => c.X), quad.Average(c => c.Y));
            if (cx >= 0 && cy >= 0 && cx <= size.Width && cy <= size.Height)
            {
                quads.Add(quad);
            }
        }

        return quads;
    }

    private static ModelMarker? Marker(JsonNode? node)
    {
        if (node?["synthetic"]?.GetValue<bool>() == true || node?["facet"]?.GetValue<string>() is not { } facet
            || node["cornersPlaneMm"] is not JsonArray { Count: 4 } corners)
        {
            return null;
        }

        var points = corners
            .Select(c => c is JsonArray { Count: >= 2 } p ? (p[0]!.GetValue<double>(), p[1]!.GetValue<double>()) : ((double, double)?)null)
            .ToList();
        return points.All(p => p is not null) ? new ModelMarker(facet, points.Select(p => p!.Value).ToList()) : null;
    }
}
