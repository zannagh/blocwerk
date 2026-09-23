// <copyright file="WallCaptureVideoUpload.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Capture;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind of the walk-along video picker: the file goes straight from the browser to
/// <c>POST /api/captures/{id}/video</c> (capture-video.js), so a large video never crosses the circuit.
/// </summary>
public partial class WallCaptureVideoUpload : IDisposable
{
    private DotNetObjectReference<WallCaptureVideoUpload>? selfRef;
    private bool uploading;
    private int percent;
    private string? error;

    [Parameter]
    [EditorRequired]
    public Guid CaptureId { get; set; }

    [Parameter]
    public CaptureVideoInfo? Video { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Raised after the video was uploaded or removed, so the draft is reloaded.</summary>
    [Parameter]
    public EventCallback OnChanged { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private IWallCaptureService Captures { get; set; } = default!;

    [Inject]
    private WallCapturePipelineOptions Options { get; set; } = default!;

    private long MaxMb => Options.MaxVideoBytes / (1024 * 1024);

    private int MaxFrames => Options.MaxVideoFrames;

    /// <summary>Called from capture-video.js while the file streams up.</summary>
    [JSInvokable]
    public Task OnVideoUploadProgress(int pct)
    {
        percent = Math.Clamp(pct, 0, 100);
        return InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        selfRef?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string FormatSize(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

    private async Task UploadAsync(ChangeEventArgs e)
    {
        if (uploading)
        {
            return;
        }

        uploading = true;
        percent = 0;
        error = null;
        try
        {
            selfRef ??= DotNetObjectReference.Create(this);
            var result = await JS.InvokeAsync<CaptureVideoUploadResult>(
                "bwCaptureVideo.upload", "capture-video", $"/api/captures/{CaptureId}/video", selfRef);
            if (!result.Ok)
            {
                error = result.Error ?? "The video could not be uploaded.";
            }
        }
        catch (JSException ex)
        {
            error = ex.Message;
        }
        finally
        {
            uploading = false;
        }

        await OnChanged.InvokeAsync();
    }

    private async Task RemoveAsync()
    {
        error = null;
        try
        {
            await Captures.RemoveVideoAsync(CaptureId);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }

        await OnChanged.InvokeAsync();
    }
}
