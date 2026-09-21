// <copyright file="HoldTouchupSurface.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The consumer-side state behind a <see cref="HoldTouchupToolbar"/>: the active tool, the size a
/// newly added hold gets, and which hold is selected. The toolbar is a pure control surface, so this
/// state used to be re-declared (with its select / sample / reset rules) in every consumer — three
/// more of them once the review steppers gained the toolbar too. It lives here once instead, so
/// "picking Add clears the selection" and "the pipette lands you back in Add" cannot drift between
/// the overview panes and the steppers.
/// <para>
/// It owns no persistence: every consumer still routes its add / move / resize / delete through its
/// own callbacks, which is what keeps the <c>needsReview</c> asymmetry (the touch-up step's false,
/// the carryover's true) where it belongs.
/// </para>
/// </summary>
public sealed class HoldTouchupSurface
{
    /// <summary>Normalized default radius for a user-added hold (~2% of the panel width).</summary>
    public const double DefaultRadius = 0.02;

    /// <summary>The tool currently in effect.</summary>
    public HoldTouchupTool Tool { get; private set; }

    /// <summary>The size a newly added hold gets, and what the toolbar's slider shows.</summary>
    public double Radius { get; private set; } = DefaultRadius;

    /// <summary>The selected hold, which is what the size slider resizes.</summary>
    public Guid? SelectedHoldId { get; private set; }

    /// <summary>
    /// True while a tool owns taps on the photo. The steppers read it to decide whether the editable
    /// overlay exists at all, so with no tool picked they behave exactly as they did before.
    /// </summary>
    public bool Active => Tool != HoldTouchupTool.None;

    /// <summary>True while a tap on empty photo should place a hold.</summary>
    public bool AddMode => Tool == HoldTouchupTool.Add;

    /// <summary>True while a one-finger drag should move a hold rather than pan the image.</summary>
    public bool MoveMode => Tool == HoldTouchupTool.Move;

    /// <summary>True while hit targets must be exact, because the tap deletes what it lands on.</summary>
    public bool DeleteMode => Tool == HoldTouchupTool.Delete;

    /// <summary>Mutually exclusive tool switch, mirroring the wall editor's SetMode.</summary>
    public void SetTool(HoldTouchupTool tool)
    {
        Tool = tool;
        if (tool is HoldTouchupTool.Add or HoldTouchupTool.Pipette)
        {
            SelectedHoldId = null;
        }
    }

    /// <summary>Picks a tool, or leaves tool mode when it is already the active one.</summary>
    public void Toggle(HoldTouchupTool tool) => SetTool(Tool == tool ? HoldTouchupTool.None : tool);

    /// <summary>
    /// Leaves tool mode entirely and drops the selection. Used for the mutual exclusion with the
    /// steppers' own tap modes (re-target, moved-pick, manual pairing): entering one leaves the other,
    /// so a tap on the photo never means two things at once.
    /// </summary>
    public void Reset()
    {
        Tool = HoldTouchupTool.None;
        SelectedHoldId = null;
    }

    /// <summary>
    /// Selects a hold AND adopts its radius, exactly as the wall editor does on select: otherwise the
    /// slider would still show the last add-size while its label read "Size (selected)", and one nudge
    /// would resize the hold to a value the user never chose.
    /// </summary>
    public void Select(Guid id, IReadOnlyList<PanelHold> holds)
    {
        SelectedHoldId = id;
        if (holds.FirstOrDefault(h => h.Id == id) is { } hold)
        {
            Radius = hold.Radius;
        }
    }

    /// <summary>
    /// Pipette: adopt the tapped hold's radius and drop into Add mode, which is what the sample is
    /// for. The selection is cleared so the slider describes the NEXT hold, not the sampled one.
    /// </summary>
    public void Sample(Guid id, IReadOnlyList<PanelHold> holds)
    {
        if (holds.FirstOrDefault(h => h.Id == id) is { } hold)
        {
            Radius = hold.Radius;
        }

        SelectedHoldId = null;
        Tool = HoldTouchupTool.Add;
    }

    /// <summary>Live slider feedback: the size a new hold gets always follows the slider.</summary>
    public void SetRadius(double radius) => Radius = radius;

    /// <summary>
    /// A hold was just added: select it so the slider can fine-tune it straight away. The Add tool
    /// deliberately stays active, so a run of missed holds can be placed in one go.
    /// </summary>
    public void Added(Guid id) => SelectedHoldId = id;

    /// <summary>A hold went away; drop it from the selection so the slider has no dead target.</summary>
    public void Removed(Guid id)
    {
        if (SelectedHoldId == id)
        {
            SelectedHoldId = null;
        }
    }

    /// <summary>
    /// The tool a key selects, or null when the key is not a tool key. The letters match the wizard's
    /// (a / m / d / p) and, where they exist there, the wall editor's, so the same key means the same
    /// tool on every surface that binds them.
    /// </summary>
    public static HoldTouchupTool? ToolForKey(string key) => key switch
    {
        "a" or "A" => HoldTouchupTool.Add,
        "m" or "M" => HoldTouchupTool.Move,
        "d" or "D" => HoldTouchupTool.Delete,
        "p" or "P" => HoldTouchupTool.Pipette,
        _ => null,
    };
}
