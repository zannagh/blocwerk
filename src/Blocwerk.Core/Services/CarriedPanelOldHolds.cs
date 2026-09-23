// <copyright file="CarriedPanelOldHolds.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// One re-photographed panel of an in-flight big update, with the OLD-generation holds the carryover
/// matcher ran against on that panel. Hold X/Y are PANEL-normalized, so a set is only meaningful when
/// drawn over that panel's own live photo — which is exactly what this record pairs them with.
/// </summary>
/// <param name="Col">The panel's grid column; the centre is (0,0).</param>
/// <param name="Row">The panel's grid row.</param>
/// <param name="LivePanelId">
/// The live (pre-update) panel at this position, whose photo is the carryover review's "before" image.
/// Null only when the position has no live panel yet (a panel added by this update).
/// </param>
/// <param name="StagedPanelId">The staged panel at this position, whose photo is the "after" image.</param>
/// <param name="OldHolds">
/// The old live holds on this panel that the update will carry — after the updated-panel scope and the
/// crash-mat false-positive drop, i.e. exactly the set the matcher and the promote work with.
/// </param>
/// <param name="AlignmentFailed">
/// True when the matcher could not line this panel's new photo up with its previous one (in this run or an
/// earlier one of the same session): every old hold on it is carried at its OLD coordinates, unconfirmed,
/// and the promote flags those blind carries for review.
/// </param>
public record CarriedPanelOldHolds(
    int Col,
    int Row,
    Guid? LivePanelId,
    Guid StagedPanelId,
    IReadOnlyList<PanelHold> OldHolds,
    bool AlignmentFailed = false);
