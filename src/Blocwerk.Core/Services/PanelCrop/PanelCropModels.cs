// <copyright file="PanelCropModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>A boulder that uses a hold a crop would remove.</summary>
/// <param name="Id">The boulder.</param>
/// <param name="Name">Its name.</param>
/// <param name="Grade">Its grade, if any.</param>
/// <param name="IsActive">False when it is already archived or historic (it loses the hold but changes state no further).</param>
public sealed record PanelCropAffectedBoulder(Guid Id, string Name, string? Grade, bool IsActive);

/// <summary>What a crop would do to the panel's live holds.</summary>
/// <param name="RemovedHoldIds">The holds the crop cuts off (centre outside, or outline mostly outside).</param>
/// <param name="KeptHoldCount">The live holds that stay and are re-mapped.</param>
/// <param name="Boulders">The boulders that use a removed hold.</param>
public sealed record PanelCropPreview(
    IReadOnlyList<Guid> RemovedHoldIds,
    int KeptHoldCount,
    IReadOnlyList<PanelCropAffectedBoulder> Boulders)
{
    /// <summary>Whether saving needs the "are you sure?" confirmation.</summary>
    public bool NeedsConfirmation => RemovedHoldIds.Count > 0;
}

/// <summary>The outcome of a crop or an undo.</summary>
/// <param name="Applied">False when the crop was not applied because it removes holds and was not confirmed.</param>
/// <param name="Preview">What the crop does (or would do) to the holds; empty for an undo.</param>
/// <param name="PhotoRevision">The panel's photo revision afterwards (cache-bust token for the photo URL).</param>
/// <param name="HistoricBoulderCount">How many active boulders the removed holds made historic.</param>
public sealed record PanelCropResult(bool Applied, PanelCropPreview Preview, int PhotoRevision, int HistoricBoulderCount);

/// <summary>A live panel's crop state, for the editor.</summary>
/// <param name="IsCropped">Whether the photo is cropped (an original is kept and "Undo crop" is available).</param>
/// <param name="Crop">The current crop relative to the original, when cropped.</param>
/// <param name="PhotoRevision">The panel's photo revision.</param>
public sealed record PanelCropState(bool IsCropped, PanelCropRect? Crop, int PhotoRevision);
