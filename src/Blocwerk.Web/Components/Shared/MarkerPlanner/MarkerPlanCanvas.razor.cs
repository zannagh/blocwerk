// <copyright file="MarkerPlanCanvas.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>
/// Code-behind for the net canvas: attaches <c>marker-planner.js</c> to the SVG once and relays its
/// clicks and drops (net coordinates, mm, y up) to the page.
/// </summary>
public partial class MarkerPlanCanvas : IAsyncDisposable
{
    private ElementReference svg;
    private IJSObjectReference? module;
    private IJSObjectReference? handle;
    private DotNetObjectReference<MarkerPlanCanvas>? selfRef;

    /// <summary>The drawing to show.</summary>
    [Parameter]
    [EditorRequired]
    public NetCanvasModel Model { get; set; } = default!;

    /// <summary>Highlighted surface, if any.</summary>
    [Parameter]
    public int? SelectedSegment { get; set; }

    /// <summary>Highlighted marker, if any.</summary>
    [Parameter]
    public int? SelectedMarker { get; set; }

    /// <summary>True while a click on a surface should add a marker (only changes the cursor here).</summary>
    [Parameter]
    public bool AddMode { get; set; }

    /// <summary>A surface (index, or -1 for empty canvas) was clicked at a net point.</summary>
    [Parameter]
    public EventCallback<CanvasClick> OnCanvasClick { get; set; }

    /// <summary>A marker was tapped without moving it.</summary>
    [Parameter]
    public EventCallback<int> OnMarkerClick { get; set; }

    /// <summary>A marker was dragged; the point is its new centre in the net.</summary>
    [Parameter]
    public EventCallback<CanvasClick> OnMarkerDrop { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ILogger<MarkerPlanCanvas> Logger { get; set; } = default!;

    /// <summary>Called by marker-planner.js.</summary>
    [JSInvokable]
    public Task OnCanvasClicked(int segment, double netX, double netY) =>
        OnCanvasClick.InvokeAsync(new CanvasClick(segment, netX, netY));

    /// <summary>Called by marker-planner.js.</summary>
    [JSInvokable]
    public Task OnMarkerClicked(int id) => OnMarkerClick.InvokeAsync(id);

    /// <summary>Called by marker-planner.js; <paramref name="id"/> travels in <see cref="CanvasClick.Target"/>.</summary>
    [JSInvokable]
    public Task OnMarkerDropped(int id, double netX, double netY) =>
        OnMarkerDrop.InvokeAsync(new CanvasClick(id, netX, netY));

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (handle is not null)
            {
                await handle.InvokeVoidAsync("dispose");
                await handle.DisposeAsync();
            }

            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; the browser dropped the listeners with the page.
        }

        selfRef?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            selfRef = DotNetObjectReference.Create(this);
            module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/marker-planner.js");
            handle = await module.InvokeAsync<IJSObjectReference>("attach", svg, selfRef);
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Marker planner canvas could not attach its pointer handling");
        }
    }

    private static string F(double value) => NetCanvasModel.F(value);
}
