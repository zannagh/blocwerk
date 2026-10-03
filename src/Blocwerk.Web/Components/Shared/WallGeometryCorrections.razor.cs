// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>Code-behind of the model corrections: loads the state per active model and routes every correction through the service.</summary>
public partial class WallGeometryCorrections
{
    private Guid loadedModelId;
    private GeometryCorrectionState? state;
    private string? failure;
    private bool busy;
    private bool scaleOpen;
    private bool surfacesOpen;
    private string? confirmDrop;

    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>The active model; a new one (a capture, an activation, a correction) reloads the state.</summary>
    [Parameter]
    public Guid ModelId { get; set; }

    /// <summary>Raised with the summary once a correction made a new model version active.</summary>
    [Parameter]
    public EventCallback<string> OnCorrected { get; set; }

    [Inject]
    private IWallGeometryCorrectionService Corrections { get; set; } = default!;

    [Inject]
    private ILogger<WallGeometryCorrections> Logger { get; set; } = default!;

    protected override async Task OnParametersSetAsync()
    {
        if (ModelId == loadedModelId)
        {
            return;
        }

        loadedModelId = ModelId;
        failure = null;
        confirmDrop = null;
        try
        {
            state = await Corrections.GetStateAsync(WallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load the model corrections of wall {WallId}", WallId);
            state = null;
        }
    }

    private Task MakeExactAsync(CaptureScaleReference reference) => RunAsync(() => Corrections.MakeSizesExactAsync(WallId, reference));

    private Task VerticalAsync(string facetId) => RunAsync(() => Corrections.SetVerticalSurfaceAsync(WallId, facetId));

    private Task DropAsync(string facetId) => RunAsync(() => Corrections.DropSurfaceAsync(WallId, facetId));

    private async Task RunAsync(Func<Task<GeometryCorrectionResult>> correction)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        failure = null;
        try
        {
            var result = await correction();
            confirmDrop = null;
            scaleOpen = false;
            surfacesOpen = false;
            await OnCorrected.InvokeAsync(result.Summary);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "A correction of wall {WallId}'s model failed", WallId);
            failure = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }
}
