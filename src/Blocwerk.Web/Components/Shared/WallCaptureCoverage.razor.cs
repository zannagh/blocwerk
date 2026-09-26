// <copyright file="WallCaptureCoverage.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>A capture's "Next capture: what to add" list with its per-facet heatmaps, read when opened.</summary>
public partial class WallCaptureCoverage
{
    private const string TipKey = "blocwerk-markerless-markers-tip";

    private Guid loadedCaptureId;
    private CaptureCoverageReport? report;
    private string? failure;
    private bool open;
    private bool loading;
    private bool tipDismissed;

    [Parameter]
    public Guid WallId { get; set; }

    [Parameter]
    public Guid CaptureId { get; set; }

    /// <summary>Open (and read) right away: the wall's latest capture in the settings.</summary>
    [Parameter]
    public bool StartOpen { get; set; }

    /// <summary>Shown as a card of its own rather than a line in a history row.</summary>
    [Parameter]
    public bool Prominent { get; set; }

    /// <summary>The collapsed button's text.</summary>
    [Parameter]
    public string Title { get; set; } = "Next capture: what to add";

    [Inject]
    private ICaptureCoverageService Coverage { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    protected override async Task OnParametersSetAsync()
    {
        if (CaptureId == loadedCaptureId)
        {
            return;
        }

        loadedCaptureId = CaptureId;
        report = null;
        failure = null;
        open = StartOpen;
        if (open)
        {
            await LoadAsync();
        }
    }

    private static string SourceLine(CaptureCoverageReport r)
    {
        var views = r.VideoViews > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{r.PhotoViews} photos and {r.VideoViews} video frames")
            : string.Create(CultureInfo.InvariantCulture, $"{r.PhotoViews} photos");
        var video = r.Video switch
        {
            { HasVideo: false } => "no video",
            { FramesRegistered: { } placed } v => string.Create(CultureInfo.InvariantCulture, $"{placed} of {v.FramesExtracted} video frames placed"),
            var v => string.Create(CultureInfo.InvariantCulture, $"{v.FramesExtracted} video frames"),
        };
        var passes = r.Video.PosesFrom == CoveragePoseSource.Photos && r.Video.HasVideo
            ? " The video's camera positions are not reported yet, so its passes were checked on the photos."
            : string.Empty;
        return $"Rated from {views} ({video}), in 20 cm squares.{passes}";
    }

    private async Task ToggleAsync()
    {
        open = !open;
        if (open && report is null)
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        loading = true;
        try
        {
            var lookup = await Coverage.GetAsync(WallId, CaptureId);
            report = lookup.Report;
            tipDismissed = report?.FromFeatures == true && await ReadTipDismissedAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or UserFacingException or KioskRestrictedException)
        {
            failure = ex.Message;
        }
        finally
        {
            loading = false;
        }
    }

    // A per-viewer convenience: the optional markers tip, once dismissed, stays away (browser storage may be unavailable).
    private async Task<bool> ReadTipDismissedAsync()
    {
        try
        {
            return await JS.InvokeAsync<string?>("localStorage.getItem", TipKey) == "1";
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task DismissTipAsync()
    {
        tipDismissed = true;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", TipKey, "1");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // Not remembered; dismissed for now.
        }
    }
}
