// <copyright file="WallPhotoEditor.MergeSplit.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared.HoldTools;
using Blocwerk.Web.Components.Shared.Toolbar;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The merge tool (F) and the split tool (V): what they put on the toolbar, the photo and the option row, and how they
/// hook into the editor's tool switch, Escape and the hold tap. Unlike the other hold tools they do not edit the working
/// copy: they act on the SAVED holds through <see cref="IHoldMergeSplitService"/> (one undoable journal batch each), so
/// they refuse while there are unsaved edits and ask the host to reload afterwards (<see cref="OnHoldsRestructured"/>).
/// The server calls live in <c>WallPhotoEditor.MergeSplitActions.cs</c>.
/// </summary>
public partial class WallPhotoEditor
{
    private const string MergeIconMarkup = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" width=\"20\" height=\"20\"><circle cx=\"8.5\" cy=\"12\" r=\"5\" /><circle cx=\"15.5\" cy=\"12\" r=\"5\" /><path d=\"M12 8.2v7.6\" stroke-dasharray=\"1.6 1.4\" /></svg>";
    private const string SplitIconMarkup = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" width=\"20\" height=\"20\"><path d=\"M12 4c4.4 0 8 3.6 8 8s-3.6 8-8 8-8-3.6-8-8 3.6-8 8-8z\" /><path d=\"M15 2.5 9 21.5\" stroke-dasharray=\"2.4 1.6\" /></svg>";

    private readonly HoldMergeToolState mergeTool = new();
    private readonly HoldSplitToolState splitTool = new();

    /// <summary>
    /// The host reloads the holds after a merge, split or undo changed them on the server. The tools are offered only
    /// where a host listens.
    /// </summary>
    [Parameter]
    public EventCallback OnHoldsRestructured { get; set; }

    [Inject]
    private IHoldMergeSplitService MergeSplit { get; set; } = default!;

    private bool MergeSplitAvailable => HoldToolsAvailable && !IsStaging && !ManualAlignment && OnHoldsRestructured.HasDelegate;

    private ToolbarItem MergeHoldsItem => new()
    {
        Id = "merge-holds", Kind = ToolbarItemKind.Tool, Tool = HoldTouchupTool.MergeHolds, Label = "Merge two holds into one",
        ShortcutKey = "F", Icon = b => b.AddMarkupContent(0, MergeIconMarkup), Visible = () => MergeSplitAvailable, Row = RowMergeHolds,
    };

    private ToolbarItem SplitHoldItem => new()
    {
        Id = "split-hold", Kind = ToolbarItemKind.Tool, Tool = HoldTouchupTool.SplitHold, Label = "Split a hold in two",
        ShortcutKey = "V", Icon = b => b.AddMarkupContent(0, SplitIconMarkup), Visible = () => MergeSplitAvailable, Row = RowSplitHold,
    };

    private string? MergeSplitCursor => _mode switch
    {
        HoldTouchupTool.MergeHolds => "bw-cursor-pointer",
        HoldTouchupTool.SplitHold => "bw-cursor-crosshair",
        _ => null,
    };

    private bool HasMergeSplitSubMode => mergeTool.FirstId is not null || mergeTool.Refusal is not null || splitTool.HoldId is not null;

    private RenderFragment MergeSplitOverlay => builder =>
    {
        builder.OpenComponent<HoldToolsOverlay>(0);
        builder.AddComponentParameter(1, nameof(HoldToolsOverlay.Merge), _mode == HoldTouchupTool.MergeHolds ? mergeTool : null);
        builder.AddComponentParameter(2, nameof(HoldToolsOverlay.Split), _mode == HoldTouchupTool.SplitHold ? splitTool : null);
        builder.AddComponentParameter(3, nameof(HoldToolsOverlay.SplitArmed), _mode == HoldTouchupTool.SplitHold && splitTool.HoldId is not null);
        builder.CloseComponent();
    };

    private RenderFragment RowMergeHolds => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "option-bar");
        builder.OpenComponent<HoldMergeRow>(2);
        builder.AddComponentParameter(3, nameof(HoldMergeRow.State), mergeTool);
        builder.AddComponentParameter(4, nameof(HoldMergeRow.FirstLabel), LabelOf(mergeTool.FirstId, "the first hold"));
        builder.AddComponentParameter(5, nameof(HoldMergeRow.KeepLabel), Capitalized(LabelOf(mergeTool.KeepId, "the first hold")));
        builder.AddComponentParameter(6, nameof(HoldMergeRow.RemoveLabel), LabelOf(mergeTool.RemoveId, "the other hold"));
        builder.AddComponentParameter(7, nameof(HoldMergeRow.PendingChanges), _pendingChanges);
        builder.AddComponentParameter(8, nameof(HoldMergeRow.OnConfirm), EventCallback.Factory.Create(this, ConfirmMergeHoldsAsync));
        builder.AddComponentParameter(9, nameof(HoldMergeRow.OnCancel), EventCallback.Factory.Create(this, mergeTool.Reset));
        builder.AddComponentParameter(10, nameof(HoldMergeRow.OnSwap), EventCallback.Factory.Create(this, SwapMergeKeep));
        builder.AddComponentParameter(11, nameof(HoldMergeRow.OnUndo), EventCallback.Factory.Create(this, UndoMergeHoldsAsync));
        builder.CloseComponent();
        builder.CloseElement();
    };

    private RenderFragment RowSplitHold => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "option-bar");
        builder.OpenComponent<HoldSplitRow>(2);
        builder.AddComponentParameter(3, nameof(HoldSplitRow.State), splitTool);
        builder.AddComponentParameter(4, nameof(HoldSplitRow.HoldLabel), LabelOf(splitTool.HoldId, "the hold"));
        builder.AddComponentParameter(5, nameof(HoldSplitRow.PendingChanges), _pendingChanges);
        builder.AddComponentParameter(6, nameof(HoldSplitRow.OnConfirm), EventCallback.Factory.Create(this, ConfirmSplitHoldAsync));
        builder.AddComponentParameter(7, nameof(HoldSplitRow.OnCancel), EventCallback.Factory.Create(this, CancelSplitAsync));
        builder.AddComponentParameter(8, nameof(HoldSplitRow.OnUndo), EventCallback.Factory.Create(this, UndoSplitHoldAsync));
        builder.AddComponentParameter(9, nameof(HoldSplitRow.OnToggleBoulder), EventCallback.Factory.Create<Guid>(this, ToggleSplitBoulder));
        builder.CloseComponent();
        builder.CloseElement();
    };

    /// <summary>How the picked holds are drawn: the merge's kept hold green, the one that goes red dashed; the hold being cut white.</summary>
    private (string Stroke, string Dash)? MergeSplitPreview(Hold hold)
    {
        if (_mode == HoldTouchupTool.MergeHolds)
        {
            if (mergeTool.Preview is not null && mergeTool.KeepId is { } keep)
            {
                return hold.Id == keep ? (PreviewStays, string.Empty) : hold.Id == mergeTool.RemoveId ? (PreviewGoes, "0.5 0.35") : null;
            }

            return hold.Id == mergeTool.FirstId || hold.Id == mergeTool.SecondId ? ("#ffd700", string.Empty) : null;
        }

        return _mode == HoldTouchupTool.SplitHold && hold.Id == splitTool.HoldId ? ("#ffffff", "0.8 0.4") : null;
    }

    /// <summary>The tap on a hold while one of the two tools is picked.</summary>
    private Task OnMergeSplitHoldClickAsync(Hold hold) =>
        _mode == HoldTouchupTool.MergeHolds ? PickMergeHoldAsync(hold) : PickSplitHoldAsync(hold);

    /// <summary>Leaving a tool drops its picks (the Undo of the last action stays with the tool).</summary>
    private void ClearMergeSplitState(HoldTouchupTool mode)
    {
        if (mode != HoldTouchupTool.MergeHolds)
        {
            mergeTool.Reset();
        }

        if (mode != HoldTouchupTool.SplitHold && splitTool.HoldId is not null)
        {
            splitTool.Reset();
            _ = InvokeAsync(DisarmSplitAsync);
        }
    }

    /// <summary>Escape: the drawn cut first, then the picked hold, then the merge's picks.</summary>
    private bool CancelMergeSplitSubMode()
    {
        if (splitTool.Stroke is not null)
        {
            splitTool.ResetStroke();
            _ = InvokeAsync(ClearSplitDrawingAsync);
            return true;
        }

        if (splitTool.HoldId is not null)
        {
            splitTool.Reset();
            _ = InvokeAsync(DisarmSplitAsync);
            return true;
        }

        if (mergeTool.FirstId is not null || mergeTool.Refusal is not null)
        {
            mergeTool.Reset();
            return true;
        }

        return false;
    }

    private string LabelOf(Guid? holdId, string fallback) => HoldById(holdId) is { } hold ? StackLabel(hold) : fallback;

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
