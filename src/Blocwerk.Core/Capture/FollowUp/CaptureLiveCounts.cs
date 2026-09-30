// <copyright file="CaptureLiveCounts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// What the wall shows now for a capture of the active model, replacing the numbers stored when its follow-up steps ran
/// (<see cref="CaptureFollowUpText.Summary(CaptureFollowUpRecord, CaptureLiveCounts?)"/>). A null count keeps the stored text.
/// </summary>
/// <param name="Proposals">Pending hold proposals the review list shows (not covered by a live hold).</param>
/// <param name="PlacedFromPhotos">Live holds placed on the model from their panel photos (texture registration).</param>
/// <param name="CarriedFromPhotos">Of those, the ones carried over from the previous model.</param>
/// <param name="Unmeasured">Live holds the photo placement left unmeasured on purpose.</param>
/// <param name="Volumes">Visible volumes of the model (neither hidden nor removed).</param>
/// <param name="HoldsOnVolumes">Live holds currently placed on those volumes.</param>
public sealed record CaptureLiveCounts(
    int? Proposals = null,
    int? PlacedFromPhotos = null,
    int CarriedFromPhotos = 0,
    int Unmeasured = 0,
    int? Volumes = null,
    int HoldsOnVolumes = 0);
