// <copyright file="Wall3DStage.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Mounts a <see cref="Wall3DView"/> into <c>wwwroot/js/wall3d.js</c> (three.js) and disposes the
/// viewer (and its WebGL context) when the view changes or the component goes away.
/// </summary>
public partial class Wall3DStage : IAsyncDisposable
{
    private static readonly Dictionary<string, string> RoleColors = new()
    {
        ["Start"] = BoulderHoldColors.Start,
        ["Top"] = BoulderHoldColors.Top,
        ["Hand"] = BoulderHoldColors.Normal,
        ["Foot"] = BoulderHoldColors.Foot,
        ["ColorFoot"] = BoulderHoldColors.Foot,
    };

    private ElementReference stage;
    private IJSObjectReference? module;
    private IJSObjectReference? viewer;
    private IJSObjectReference? bridge;
    private DotNetObjectReference<Wall3DStage>? selfRef;
    private Wall3DView? mounted;
    private bool mountFailed;

    /// <summary>The view to render. A new instance remounts the viewer.</summary>
    [Parameter]
    [EditorRequired]
    public Wall3DView View { get; set; } = null!;

    /// <summary>Start mode: "schematic" (default), "photos" or "photoreal"; unavailable ones fall back to schematic.</summary>
    [Parameter]
    public string? InitialMode { get; set; }

    /// <summary>Start camera preset ("front", "below", "left", "right", "top").</summary>
    [Parameter]
    public string InitialPreset { get; set; } = "front";

    /// <summary>Show the gesture hint only until the viewer first touches a 3D view (remembered per browser).</summary>
    [Parameter]
    public bool HintOnce { get; set; }

    /// <summary>Raised with the surface (facet id, or null for none) under every tap; set only for the model corrections.</summary>
    [Parameter]
    public EventCallback<string?> OnFacetTap { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [Inject]
    private ILogger<Wall3DStage> Logger { get; set; } = null!;

    public async ValueTask DisposeAsync()
    {
        await UnmountAsync();
        selfRef?.Dispose();
        if (bridge is not null)
        {
            try
            {
                await bridge.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already torn down.
            }
        }

        if (module is not null)
        {
            try
            {
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already torn down.
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Called from wwwroot/js/wall3d-bridge.js with the surface under a tap.</summary>
    /// <param name="facetId">The facet id, or null.</param>
    /// <returns>A task.</returns>
    [JSInvokable]
    public Task FacetTapped(string? facetId) => OnFacetTap.InvokeAsync(facetId);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (ReferenceEquals(mounted, View))
        {
            return;
        }

        await UnmountAsync();
        mounted = View;
        mountFailed = false;
        try
        {
            module ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/wall3d.js");
            viewer = await module.InvokeAsync<IJSObjectReference>(
                "mount",
                stage,
                View,
                new Dictionary<string, object?>
                {
                    ["roleColors"] = RoleColors,
                    ["initialPreset"] = InitialPreset,
                    ["initialMode"] = InitialMode ?? "schematic",
                    ["hintOnce"] = HintOnce,
                });
            await ListenFacetTapsAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit went away mid-mount; nothing to render into any more.
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "3D view of wall {WallId} failed to start (WebGL unavailable?)", View.WallId);
            mountFailed = true;
            StateHasChanged();
        }
    }

    private async Task ListenFacetTapsAsync()
    {
        if (!OnFacetTap.HasDelegate || viewer is null)
        {
            return;
        }

        selfRef ??= DotNetObjectReference.Create(this);
        bridge ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/wall3d-bridge.js");
        await bridge.InvokeVoidAsync("listenFacetTaps", viewer, selfRef);
    }

    private async Task UnmountAsync()
    {
        var current = viewer;
        viewer = null;
        if (current is null)
        {
            return;
        }

        try
        {
            await current.InvokeVoidAsync("dispose");
            await current.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The browser side is already gone, and its WebGL context with it.
        }
    }
}
