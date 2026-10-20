// <copyright file="VolumeInset3D.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>The hybrid zoom's volume inset: opened from hybrid-zoom.js, lazily loads the 3D view and shows the volume in it.</summary>
public partial class VolumeInset3D
{
    /// <summary>The inset's orbit around the volume (wall3d-locate.js <c>clampOrbit</c>).</summary>
    private static readonly IReadOnlyDictionary<string, object?> Orbit = new Dictionary<string, object?>
    {
        ["orbit"] = new { yawDeg = 60, pitchDeg = 65, minDistance = 300, maxDistance = 2500, frontDistance = 1000 },
    };

    private readonly IReadOnlyDictionary<string, object?> orbit = Orbit;
    private ElementReference slot;
    private ElementReference inset;
    private Wall3DStage? stage;
    private DotNetObjectReference<VolumeInset3D>? selfRef;
    private WallSpot? spot;
    private string? spotText;
    private Wall3DViewResult? view;
    private string mode = "photoreal";
    private bool registered;
    private bool watching;

    /// <summary>The wall.</summary>
    [Parameter]
    [EditorRequired]
    public Guid WallId { get; set; }

    /// <summary>The share token, on a share-link page.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [Inject]
    private IWall3DViewService ViewService { get; set; } = null!;

    private string ThreeDUrl => string.IsNullOrEmpty(ShareToken)
        ? $"/walls/{WallId}/3d"
        : $"/walls/{WallId}/shared/{Uri.EscapeDataString(ShareToken)}/3d";

    /// <summary>Called by hybrid-zoom.js when the "Look underneath" chip is tapped.</summary>
    /// <param name="at">The spot (<c>facet:a:b:h:under</c>).</param>
    /// <returns>A task.</returns>
    [JSInvokable]
    public async Task OpenAsync(string at)
    {
        if (WallSpot.Parse(at) is not { } parsed || at == spotText)
        {
            return;
        }

        (spot, spotText) = (parsed, at);
        StateHasChanged();
        if (view is null)
        {
            try
            {
                view = await ViewService.BuildAsync(WallId, null, ShareToken);
            }
            catch (UnauthorizedAccessException)
            {
                view = new Wall3DViewResult(Wall3DViewStatus.NotFound);
            }

            StateHasChanged();
        }
    }

    /// <summary>Called by hybrid-zoom.js on Escape, a tap outside, the panel changing or zooming out.</summary>
    /// <returns>A task.</returns>
    [JSInvokable]
    public Task CloseFromJs() => InvokeAsync(CloseAsync);

    public async ValueTask DisposeAsync()
    {
        await UnwatchAsync();
        selfRef?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (!registered)
            {
                selfRef ??= DotNetObjectReference.Create(this);
                var light = await JS.InvokeAsync<bool>("bwHybridZoom.registerInset", slot, selfRef);
                (registered, mode) = (true, light ? "schematic" : "photoreal");
            }

            if (spot is not null && !watching)
            {
                watching = true;
                await JS.InvokeVoidAsync("bwHybridZoom.watchInset", inset, slot, selfRef);
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException)
        {
            // No browser to talk to: the chip stays a plain link to the 3D page.
        }
    }

    private async Task CloseAsync()
    {
        if (spot is null)
        {
            return;
        }

        (spot, spotText, stage) = (null, null, null);
        await UnwatchAsync();
        StateHasChanged();
    }

    private Task LookAsync(bool under) => stage?.LookAtSpotAsync(under) ?? Task.CompletedTask;

    private async Task UnwatchAsync()
    {
        if (!watching)
        {
            return;
        }

        watching = false;
        try
        {
            await JS.InvokeVoidAsync("bwHybridZoom.unwatchInset");
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException)
        {
            // Listeners die with the page.
        }
    }
}
