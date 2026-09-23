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
    /// <summary>
    /// How long the .NET side waits for the upload promise. Blazor's default JS interop timeout is one
    /// minute, far shorter than a large video takes; this sits just above capture-video.js's own
    /// 120-minute XHR timeout so the browser always reports first.
    /// </summary>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(125);

    private DotNetObjectReference<WallCaptureVideoUpload>? selfRef;
    private bool uploading;
    private int percent;
    private double loadedMb;
    private double totalMb;
    private int? etaSeconds;
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
    public Task OnVideoUploadProgress(int pct, double loaded, double total, int? eta)
    {
        percent = Math.Clamp(pct, 0, 100);
        loadedMb = Math.Max(0, loaded);
        totalMb = Math.Max(0, total);
        etaSeconds = eta is >= 0 ? eta : null;
        return InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        selfRef?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string FormatSize(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

    private static string FormatEta(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s left",
        < 3600 => $"{seconds / 60} min {seconds % 60:00} s left",
        _ => $"{seconds / 3600} h {seconds / 60 % 60:00} min left",
    };

    private string ProgressText()
    {
        if (percent >= 100)
        {
            return "Checking the video…";
        }

        var text = string.Create(
            CultureInfo.InvariantCulture, $"Uploading video… {loadedMb:0} of {totalMb:0} MB ({percent} %)");
        return etaSeconds is { } eta ? $"{text}, about {FormatEta(eta)}" : text;
    }

    private async Task UploadAsync(ChangeEventArgs e)
    {
        if (uploading)
        {
            return;
        }

        uploading = true;
        percent = 0;
        loadedMb = 0;
        totalMb = 0;
        etaSeconds = null;
        error = null;
        try
        {
            selfRef ??= DotNetObjectReference.Create(this);
            var result = await JS.InvokeAsync<CaptureVideoUploadResult>(
                "bwCaptureVideo.upload", UploadTimeout, "capture-video", $"/api/captures/{CaptureId}/video", selfRef);
            if (!result.Ok)
            {
                error = result.Error ?? "The video could not be uploaded.";
            }
        }
        catch (JSException ex)
        {
            error = ex.Message;
        }
        catch (TaskCanceledException)
        {
            error = "The upload took too long and was stopped.";
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
