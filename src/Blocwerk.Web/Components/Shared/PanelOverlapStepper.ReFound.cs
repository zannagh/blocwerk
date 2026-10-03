// <copyright file="PanelOverlapStepper.ReFound.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Holds the carry-over review already made the re-found successor of an existing hold. "Delete hold" is
/// not offered on them: the promote would keep them anyway (the carry wins), so the place to drop one is
/// its carry verdict ("removed") in the carry-over review.
/// </summary>
public partial class PanelOverlapStepper
{
    private const string ReFoundDeleteHint =
        "This hold is the re-found successor of an existing hold. To remove it, mark that hold removed in the carry-over review.";

    /// <summary>Staged holds the carry-over verdicts use as an old hold's successor (null outside a big update).</summary>
    [Parameter]
    public IReadOnlySet<Guid>? ReFoundSuccessorIds { get; set; }

    /// <summary>Whether the neighbour hold of the current step is a re-found successor.</summary>
    private bool CurrentIsReFound =>
        _index < _steps.Count && ReFoundSuccessorIds is { } ids && ids.Contains(_steps[_index].HoldAId);

    private string DeleteHoldTitle => CurrentIsReFound ? ReFoundDeleteHint : "This hold was removed from the wall (X / Delete)";
}
