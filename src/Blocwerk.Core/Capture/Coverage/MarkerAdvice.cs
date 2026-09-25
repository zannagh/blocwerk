// <copyright file="MarkerAdvice.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>The marker lines of the "what to add" list.</summary>
public static class MarkerAdvice
{
    /// <summary>What to add or reshoot for a facet's markers.</summary>
    /// <param name="facet">The facet.</param>
    /// <param name="markers">Its marker coverage.</param>
    /// <returns>The lines.</returns>
    public static IEnumerable<CoverageAdvice> Lines(CoverageFacet facet, MarkerCoverage markers)
    {
        var name = CoverageWhere.Facet(facet.Name);
        if (markers.Suggestion is { Count: > 0 } s)
        {
            var add = string.Create(
                CultureInfo.InvariantCulture, $"Add {s.Count} {(s.Count == 1 ? "marker" : "markers")} ({s.SizeMm:F0} mm) on {name} at {s.Where}");
            var why = markers.FewMarkers
                ? string.Create(CultureInfo.InvariantCulture, $"it has only {markers.Markers.Count}")
                : "its markers sit close together, so its angle is poorly pinned down";
            yield return new CoverageAdvice("markers", $"{add}: {why}", facet.Id);
        }

        var weak = markers.WeakMarkers;
        if (weak.Count > 0)
        {
            var ids = CoverageWhere.List(weak.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            var subject = weak.Count == 1 ? $"Marker {ids} on {name} is" : $"Markers {ids} on {name} are";
            yield return new CoverageAdvice(
                "markers",
                $"{subject} in fewer than {MarkerCoverageRater.MinPhotos} photos: shoot {(weak.Count == 1 ? "it" : "them")} together with the neighbouring markers",
                facet.Id);
        }
    }
}
