// <copyright file="CaptureIgnoredDetections.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The detections the app left out before the solve (no quiet zone, not fitting the plan layout) join the solver's own
/// rejections in the model's <c>quality.rejectedObservations</c>, so the solver notes list every ignored marker with
/// its reason ("Ignored marker 17 in p46: doesn't fit the plan layout …").
/// </summary>
internal static class CaptureIgnoredDetections
{
    /// <summary>The solved model JSON with the capture's ignored detections appended; unchanged when there are none.</summary>
    /// <param name="solvedJson">The solver's model JSON.</param>
    /// <param name="photos">The capture's photos.</param>
    public static string AddToModel(string solvedJson, IEnumerable<WallCapturePhoto> photos)
    {
        var records = photos
            .OrderBy(p => p.Index)
            .SelectMany(p => CaptureComputeDocuments.IgnoredMarkers(p.MarkersJson).Select(m => Record(p.Index, m)))
            .ToList();
        if (records.Count == 0)
        {
            return solvedJson;
        }

        try
        {
            if (JsonNode.Parse(solvedJson) is not JsonObject root)
            {
                return solvedJson;
            }

            if (root["quality"] is not JsonObject quality)
            {
                quality = [];
                root["quality"] = quality;
            }

            if (quality["rejectedObservations"] is not JsonArray list)
            {
                list = [];
                quality["rejectedObservations"] = list;
            }

            records.ForEach(r => list.Add(r));
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return solvedJson;
        }
    }

    private static JsonObject Record(int photoIndex, CaptureMarker marker) => new()
    {
        ["photo"] = CaptureComputeDocuments.PhotoName(photoIndex),
        ["id"] = marker.Id,
        ["reason"] = marker.Ignored,
        ["detail"] = marker.IgnoredDetail,
        ["markerDropped"] = false,
    };
}
