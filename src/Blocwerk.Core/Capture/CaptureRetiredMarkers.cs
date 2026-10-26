// <copyright file="CaptureRetiredMarkers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Capture detections of a retired id (<see cref="MarkerIdPolicy.Retired"/>) are ignored, never given to the solver: holds
/// decode as it, and a sheet still on the wall from an older plan would let them in again. They are flagged when
/// detected and, for photos stored before the id was retired, whenever stored markers are read
/// (<see cref="CaptureComputeDocuments.ParseMarkers"/>), so a re-solve of an old capture leaves them out too. They are
/// listed with the other ignored detections in the model's solver notes.
/// </summary>
internal static class CaptureRetiredMarkers
{
    /// <summary>The stored reason of a retired id's detection.</summary>
    public const string Reason = Geometry.WallGeometryRejectedObservation.RetiredId;

    /// <summary>The marker, flagged as ignored when it is a usable detection of a retired id.</summary>
    /// <param name="marker">A stored or freshly detected marker.</param>
    public static CaptureMarker Flag(CaptureMarker marker) => marker.Ignored is null && MarkerIdPolicy.IsRetired(marker.Id)
        ? marker with
        {
            Ignored = Reason,
            IgnoredDetail = $"id {marker.Id} is retired: dark holds with a bright bolt read as it, so it never goes to the solver",
        }
        : marker;
}
