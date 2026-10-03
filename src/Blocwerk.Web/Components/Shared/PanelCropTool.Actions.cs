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

        await OnSaved.InvokeAsync(result.PhotoRevision);
    });

    private Task CropAnywayAsync() => RunAsync(async () =>
    {
        var result = await CropService.CropAsync(WallId, PanelId, rect, confirmRemovals: true);
        confirm = null;
        await OnSaved.InvokeAsync(result.PhotoRevision);
    });

    private void CancelConfirm()
    {
        confirm = null;
    }

    private Task UndoAsync() => RunAsync(async () =>
    {
        var result = await CropService.UndoAsync(WallId, PanelId);
        await OnSaved.InvokeAsync(result.PhotoRevision);
    });

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
            error = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }
}
