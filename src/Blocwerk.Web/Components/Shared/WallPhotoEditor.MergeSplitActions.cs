// <copyright file="WallPhotoEditor.MergeSplitActions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared.HoldTools;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The merge and split tools' server calls and the split stroke's browser side (<c>hold-split-line.js</c>). Every write
/// first refuses while the editor holds unsaved edits: a merge or split works on the saved holds, and the host's reload
/// afterwards would otherwise throw those edits away.
/// </summary>
public partial class WallPhotoEditor
{
    private const int CircleOutlineSegments = 32;
    private const string UnexpectedFailure = "Something went wrong and the change may not have been made. Reload the wall to see the holds as they are.";

    private IJSObjectReference? splitModule;

    /// <summary>The finished stroke from <c>hold-split-line.js</c> (normalized), once per stroke: the server previews the cut.</summary>
    /// <param name="xs">The stroke's x coordinates.</param>
    /// <param name="ys">The stroke's y coordinates.</param>
    /// <returns>A task.</returns>
    [JSInvokable]
    public async Task OnSplitStroke(double[] xs, double[] ys)
    {
        if (_mode != HoldTouchupTool.SplitHold || splitTool.HoldId is not { } holdId || xs.Length != ys.Length || xs.Length < 2)
        {
            return;
        }

        splitTool.ResetStroke();
        splitTool.Stroke = [.. xs.Zip(ys, (x, y) => new NormalizedPoint(x, y))];
        await RunToolCallAsync(splitTool, async () =>
        {
            splitTool.Preview = await MergeSplit.PreviewSplitAsync(WallId, holdId, splitTool.Stroke);
            if (splitTool.Preview.Refusal is not null)
            {
                await ClearSplitDrawingAsync();
            }
        });
        StateHasChanged();
    }

    private async Task PickMergeHoldAsync(Hold hold)
    {
        if (mergeTool.Busy)
        {
            return;
        }

        if (mergeTool.FirstId is null || mergeTool.SecondId is not null || mergeTool.Refusal is not null)
        {
            mergeTool.Reset();
            mergeTool.FirstId = hold.Id;
            return;
        }

        if (mergeTool.FirstId == hold.Id)
        {
            mergeTool.Reset();
            return;
        }

        var first = mergeTool.FirstId.Value;
        mergeTool.SecondId = hold.Id;
        await RunMergeCallAsync(async () =>
        {
            mergeTool.Preview = await MergeSplit.PreviewMergeAsync(WallId, first, hold.Id);
            mergeTool.KeepId = mergeTool.Preview.SuggestedKeptId;
        });
    }

    private void SwapMergeKeep()
    {
        if (mergeTool.RemoveId is { } other)
        {
            mergeTool.KeepId = other;
        }
    }

    private async Task ConfirmMergeHoldsAsync()
    {
        if (_pendingChanges > 0 || mergeTool.KeepId is not { } keep || mergeTool.RemoveId is not { } remove)
        {
            return;
        }

        await RunMergeCallAsync(async () =>
        {
            var result = await MergeSplit.MergeAsync(WallId, keep, remove);
            mergeTool.Reset();
            mergeTool.LastBatchId = result.BatchId;
            await OnHoldsRestructured.InvokeAsync();
        });
    }

    private async Task UndoMergeHoldsAsync()
    {
        if (mergeTool.LastBatchId is not { } batchId || _pendingChanges > 0)
        {
            return;
        }

        await RunMergeCallAsync(async () =>
        {
            mergeTool.LastBatchId = null;
            var undo = await MergeSplit.UndoAsync(WallId, batchId);
            mergeTool.Refusal = undo.Reverted ? null : $"The merge could not be undone: {undo.Error ?? "the holds were changed since."}";
            await OnHoldsRestructured.InvokeAsync();
        });
    }

    private async Task PickSplitHoldAsync(Hold hold)
    {
        if (splitTool.Busy || splitTool.HoldId == hold.Id)
        {
            return;
        }

        splitTool.Reset();
        splitTool.HoldId = hold.Id;
        if (hold.IsVirtual)
        {
            splitTool.Error = "A virtual hold has no outline to cut.";
            await DisarmSplitAsync();
            return;
        }

        await ArmSplitAsync(hold);
    }

    private void ToggleSplitBoulder(Guid boulderId)
    {
        if (!splitTool.OnSmaller.Remove(boulderId))
        {
            splitTool.OnSmaller.Add(boulderId);
        }
    }

    private async Task ConfirmSplitHoldAsync()
    {
        if (_pendingChanges > 0 || !splitTool.Ready || splitTool.HoldId is not { } holdId || splitTool.Stroke is not { } stroke)
        {
            return;
        }

        await RunToolCallAsync(splitTool, async () =>
        {
            var result = await MergeSplit.SplitAsync(WallId, holdId, stroke, [.. splitTool.OnSmaller], splitTool.LinksOnSmaller);
            splitTool.Reset();
            splitTool.LastBatchId = result.BatchId;
            await DisarmSplitAsync();
            await OnHoldsRestructured.InvokeAsync();
        });
    }

    private async Task UndoSplitHoldAsync()
    {
        if (splitTool.LastBatchId is not { } batchId || _pendingChanges > 0)
        {
            return;
        }

        await RunToolCallAsync(splitTool, async () =>
        {
            splitTool.LastBatchId = null;
            var undo = await MergeSplit.UndoAsync(WallId, batchId);
            await OnHoldsRestructured.InvokeAsync();
            if (!undo.Reverted)
            {
                splitTool.Error = $"The split could not be undone: {undo.Error ?? "the holds were changed since."}";
            }
        });
    }

    private async Task CancelSplitAsync()
    {
        splitTool.Reset();
        await DisarmSplitAsync();
    }

    private async Task RunMergeCallAsync(Func<Task> call)
    {
        mergeTool.Busy = true;
        try
        {
            await call();
        }
        catch (Exception ex) when (ex is UserFacingException or UnauthorizedAccessException)
        {
            mergeTool.Preview = null;
            mergeTool.Refusal = ex is UserFacingException ? ex.Message : "You may not edit the holds of this wall.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else stays in this tool: an unhandled exception here would take the whole circuit down.
            MergeSplitLogger.LogError(ex, "Merge tool failed on wall {WallId}", WallId);
            mergeTool.Preview = null;
            mergeTool.Refusal = UnexpectedFailure;
        }
        finally
        {
            mergeTool.Busy = false;
        }
    }

    private async Task RunToolCallAsync(HoldSplitToolState state, Func<Task> call)
    {
        state.Busy = true;
        try
        {
            await call();
        }
        catch (Exception ex) when (ex is UserFacingException or UnauthorizedAccessException)
        {
            state.Preview = null;
            state.Error = ex is UserFacingException ? ex.Message : "You may not edit the holds of this wall.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MergeSplitLogger.LogError(ex, "Split tool failed on wall {WallId}", WallId);
            state.Preview = null;
            state.Error = UnexpectedFailure;
        }
        finally
        {
            state.Busy = false;
        }
    }

    private async Task ArmSplitAsync(Hold hold)
    {
        try
        {
            splitModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/hold-split-line.js");
            await splitModule.InvokeVoidAsync("arm", _containerRef, holdDragRef, OutlineInViewBox(hold));
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            splitTool.Error = "The drawing could not start. Pick the hold again.";
        }
    }

    private async Task DisarmSplitAsync() => await CallSplitModuleAsync("disarm");

    private async Task ClearSplitDrawingAsync() => await CallSplitModuleAsync("clear");

    private async Task CallSplitModuleAsync(string function)
    {
        if (splitModule is null)
        {
            return;
        }

        try
        {
            await splitModule.InvokeVoidAsync(function, _containerRef);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
        {
            // The page or circuit is gone; nothing is armed any more.
        }
    }

    /// <summary>The hold's drawn outline in the overlay's 0..100 viewBox: the traced polygon, or the circle.</summary>
    private static double[][] OutlineInViewBox(Hold hold)
    {
        if (hold.ShapePoints is { Count: >= 3 } shape)
        {
            return [.. shape.Select(p => new[] { (hold.X + p.Dx) * 100, (hold.Y + p.Dy) * 100 })];
        }

        return [.. Enumerable.Range(0, CircleOutlineSegments).Select(i =>
        {
            double a = 2 * Math.PI * i / CircleOutlineSegments;
            return new[] { (hold.X + (hold.Radius * Math.Cos(a))) * 100, (hold.Y + (hold.Radius * Math.Sin(a))) * 100 };
        })];
    }
}
