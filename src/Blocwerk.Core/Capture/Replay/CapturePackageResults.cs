// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// What the source already computed for the capture's model, so the target does not compute it again: the finished
/// photo-real view (no <c>splat-finish</c>), the volumes, the pending hold proposals, every live hold's 3D fields (each tagged
/// with the panel geometry it was computed from) and the follow-up record that says these steps ran.
/// </summary>
/// <param name="Splat">The model's finished photo-real view; its files are in the package.</param>
/// <param name="Volumes">The model's volumes (they depend on the view and the model only, not on the holds).</param>
/// <param name="Proposals">The pending proposals found on the model.</param>
/// <param name="Holds">Every live hold of the source wall with its 3D fields.</param>
/// <param name="FollowUpJson">The source capture's follow-up record (settled: nothing running or queued).</param>
public sealed record CapturePackageResults(
    WallGeometrySplat Splat,
    IReadOnlyList<WallVolume> Volumes,
    IReadOnlyList<HoldProposal> Proposals,
    IReadOnlyList<CapturePackageHoldResult> Holds,
    string FollowUpJson);
