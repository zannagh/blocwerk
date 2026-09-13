// <copyright file="CrossGenLinkTool.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Side-by-side CROSS-GENERATION confirm: LEFT = the previous/current-gen holds, RIGHT = the staged
/// new-gen holds. Matcher-matched pairs render pre-linked (green); the attention queue (the few
/// uncertain holds) is surfaced as a focusable stepper so the user only has to look at what matters.
/// Tapping an unlinked left hold then a right hold declares the cross-gen mapping; tapping a linked
/// left hold lets the user break it (the hold falls back to blind warp-carry, still safe).
/// This is an IN-MEMORY mapping over the parent's decisions — nothing persists here; it becomes a
/// HoldGenerationLink only at promote. Modelled on <see cref="PanelLinkTool"/>. Markup is in the .razor.
/// </summary>
public partial class CrossGenLinkTool
{
    [Parameter] public Guid WallId { get; set; }
    [Parameter] public Guid CenterPanelId { get; set; }

    /// <summary>Previous/current-gen holds (left pane), already loaded by the parent.</summary>
    [Parameter] public IReadOnlyList<PanelHold> OldHolds { get; set; } = [];

    /// <summary>Staged new-gen holds (right pane), already loaded by the parent.</summary>
    [Parameter] public IReadOnlyList<PanelHold> NewHolds { get; set; } = [];

    /// <summary>Current cross-gen mapping: old hold id → its mapped new hold id (the parent's decisions).</summary>
    [Parameter] public IReadOnlyDictionary<Guid, Guid> Links { get; set; } = new Dictionary<Guid, Guid>();

    /// <summary>The residual-ranked attention queue: the few old holds the matcher was unsure about.</summary>
    [Parameter] public IReadOnlyList<Guid> AttentionOldIds { get; set; } = [];

    /// <summary>Declare a cross-gen mapping (old → new). The parent writes it into its decisions.</summary>
    [Parameter] public EventCallback<(Guid OldHoldId, Guid NewHoldId)> OnLink { get; set; }

    /// <summary>Break an old hold's cross-gen mapping; it falls back to blind warp-carry.</summary>
    [Parameter] public EventCallback<Guid> OnUnlink { get; set; }

    [Parameter] public EventCallback OnAdvance { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private const string LinkGreen = "var(--status-success)";
    private const string AttentionAmber = "var(--status-warning)";
    private const string NewBlue = "var(--status-info)";

    private Dictionary<Guid, PanelHold> _oldById = [];
    private Dictionary<Guid, PanelHold> _newById = [];

    // Free-form manual selection (link any hold, in or out of the queue).
    private Guid? _leftHoldId;
    private Guid? _rightHoldId;

    // The focused queue entry: drives the numbered badge + the re-centre of both panels.
    private int _queueIndex;
    private int _focusKey;

    protected override void OnParametersSet()
    {
        _oldById = OldHolds.ToDictionary(h => h.Id);
        _newById = NewHolds.ToDictionary(h => h.Id);
        if (_queueIndex >= AttentionOldIds.Count)
        {
            _queueIndex = Math.Max(0, AttentionOldIds.Count - 1);
        }
    }

    // ---- Attention queue focus ----------------------------------------------------
    private bool HasQueue => AttentionOldIds.Count > 0;

    private Guid? FocusOldId => HasQueue && _queueIndex >= 0 && _queueIndex < AttentionOldIds.Count
        ? AttentionOldIds[_queueIndex]
        : null;

    private Guid? FocusNewId => FocusOldId is { } old && Links.TryGetValue(old, out var nid) ? nid : null;

    private bool FocusIsLinked => FocusOldId is { } old && Links.ContainsKey(old);

    private PanelHold? FocusOldHold => FocusOldId is { } id ? _oldById.GetValueOrDefault(id) : null;

    private PanelHold? FocusNewHold => FocusNewId is { } id ? _newById.GetValueOrDefault(id) : null;

    private static (double X, double Y)? Point(PanelHold? h) => h is null ? null : (h.X, h.Y);

    private void FocusNext()
    {
        if (_queueIndex < AttentionOldIds.Count - 1)
        {
            _queueIndex++;
        }

        ResetSelection();
    }

    private void FocusPrev()
    {
        if (_queueIndex > 0)
        {
            _queueIndex--;
        }

        ResetSelection();
    }

    private void ResetSelection()
    {
        _leftHoldId = null;
        _rightHoldId = null;
        _focusKey++;
    }

    // ---- Taps ---------------------------------------------------------------------
    private void OnLeftTap(Guid holdId) => _leftHoldId = holdId;

    private void OnRightTap(Guid holdId) => _rightHoldId = holdId;

    // The left hold whose mapping the "Break" action targets: the manual selection if it is linked,
    // else the focused queue hold if it is linked.
    private Guid? BreakableOldId =>
        _leftHoldId is { } sel && Links.ContainsKey(sel) ? sel
        : FocusIsLinked ? FocusOldId
        : null;

    private bool CanLink =>
        _leftHoldId is { } left && _rightHoldId is not null && !Links.ContainsKey(left);

    private async Task LinkSelected()
    {
        if (_leftHoldId is { } left && _rightHoldId is { } right && !Links.ContainsKey(left))
        {
            await OnLink.InvokeAsync((left, right));
            ResetSelection();
        }
    }

    private async Task BreakSelected()
    {
        if (BreakableOldId is { } old)
        {
            await OnUnlink.InvokeAsync(old);
            ResetSelection();
        }
    }

    // ---- Colour coding ------------------------------------------------------------
    // Left: green = already mapped (matcher or manual); amber = in the attention queue and NOT yet
    // mapped (needs the user's eyes); faint default otherwise. Selection/highlight always wins inside
    // PanelImageView, so these are just the resting ring colours.
    private IReadOnlyDictionary<Guid, string>? LeftHoldColors()
    {
        var attention = AttentionOldIds.ToHashSet();
        var map = new Dictionary<Guid, string>();
        foreach (var h in OldHolds)
        {
            if (Links.ContainsKey(h.Id))
            {
                map[h.Id] = LinkGreen;
            }
            else if (attention.Contains(h.Id))
            {
                map[h.Id] = AttentionAmber;
            }
        }

        return map.Count == 0 ? null : map;
    }

    // Right: green = consumed by a mapping; blue = a genuinely new staged hold.
    private IReadOnlyDictionary<Guid, string>? RightHoldColors()
    {
        var consumed = Links.Values.ToHashSet();
        var map = new Dictionary<Guid, string>();
        foreach (var h in NewHolds)
        {
            map[h.Id] = consumed.Contains(h.Id) ? LinkGreen : NewBlue;
        }

        return map;
    }

    private int LinkedCount => OldHolds.Count(h => Links.ContainsKey(h.Id));

    private string OldPhotoUrl => $"/api/walls/{WallId}/photo";
    private string NewPhotoUrl => $"/api/walls/{WallId}/panels/{CenterPanelId}/staged-photo";
}
