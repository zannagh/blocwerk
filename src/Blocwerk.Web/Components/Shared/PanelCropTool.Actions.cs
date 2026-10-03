// <copyright file="PanelCropTool.Actions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services.PanelCrop;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>The buttons of <see cref="PanelCropTool"/>: save (with the cut-off confirmation), undo, reset, close.</summary>
public partial class PanelCropTool
{
    /// <summary>Saves the crop, unless it removes holds: then it asks first (the server decides, from the stored holds).</summary>
    private Task SaveAsync() => RunAsync(async () =>
    {
        var result = await CropService.CropAsync(WallId, PanelId, rect, confirmRemovals: false);
        if (!result.Applied)
        {
            confirm = result.Preview;
            return;
        }

        await OnSaved.InvokeAsync(CroppedMessage(result));
    });

    private Task CropAnywayAsync() => RunAsync(async () =>
    {
        var result = await CropService.CropAsync(WallId, PanelId, rect, confirmRemovals: true);
        confirm = null;
        await OnSaved.InvokeAsync(CroppedMessage(result));
    });

    private void CancelConfirm()
    {
        confirm = null;
    }

    /// <summary>Undo straight away when the crop removed nothing; otherwise say first what may not come back.</summary>
    private Task UndoAsync()
    {
        if (state is { RemovedHoldCount: > 0 })
        {
            confirmUndo = true;
            return Task.CompletedTask;
        }

        return UndoConfirmedAsync();
    }

    private void CancelUndo()
    {
        confirmUndo = false;
    }

    private Task UndoConfirmedAsync() => RunAsync(async () =>
    {
        var result = await CropService.UndoAsync(WallId, PanelId);
        confirmUndo = false;
        await OnSaved.InvokeAsync(UndoneMessage(result));
    });

    private static string CroppedMessage(PanelCropResult result) => result.Preview.RemovedHoldIds.Count == 0
        ? "Panel photo cropped; holds stay where they are on the wall"
        : $"Panel photo cropped; {Plural(result.Preview.RemovedHoldIds.Count, "hold")} removed";

    private static string UndoneMessage(PanelCropResult result) => result switch
    {
        { RevertedFromJournal: true } => "Crop undone: original photo, removed holds and their boulders restored",
        { HoldsNotRestored: > 0 } => $"Crop undone (photo only): the panel was edited since, so {Plural(result.HoldsNotRestored, "removed hold")} stay deleted",
        _ => "Crop undone: original photo restored",
    };

    /// <summary>Back to the whole photo (nothing cropped yet).</summary>
    private async Task ResetAsync()
    {
        rect = PanelCropRect.Full;
        error = null;
        UpdateCutPreview();
        if (handle is not null)
        {
            await handle.InvokeVoidAsync("setRect", JsRect(rect));
        }
    }

    private Task CloseAsync() => busy ? Task.CompletedTask : OnClose.InvokeAsync();

    /// <summary>Runs a server call with the buttons disabled; a refusal (bad rectangle, update in flight) is shown inline.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        error = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            confirm = null;
            confirmUndo = false;
            error = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }
}
