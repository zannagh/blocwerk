// <copyright file="Wall3DStage.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Mounts a <see cref="Wall3DView"/> into <c>wwwroot/js/wall3d.js</c> (three.js) and disposes the
/// viewer (and its WebGL context) when the view changes or the component goes away. Mounts run one at a
/// time; a viewer whose mount returns after the view changed or the component went away is disposed at once.
/// </summary>
public partial class Wall3DStage : IAsyncDisposable
{
    // wall3d.js's MODEL_FAILED: the model could not be drawn, as opposed to WebGL missing.
    private const string ModelFailedPrefix = "wall3d-model:";

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
    private Task mounting = Task.CompletedTask;
    private bool disposed;
    private string? failure;

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
        disposed = true;
        try
        {
            // A mount in flight disposes its own viewer when it returns (it sees `disposed`).
            await mounting;
        }
        catch (OperationCanceledException)
        {
            // A JS call timed out: nothing was handed back to dispose.
        }

        await UnmountAsync();
        selfRef?.Dispose();
        await DisposeModuleAsync(bridge);
        await DisposeModuleAsync(module);
        GC.SuppressFinalize(this);
    }

    /// <summary>Called from wwwroot/js/wall3d-bridge.js with the surface under a tap.</summary>
    /// <param name="facetId">The facet id, or null.</param>
    /// <returns>A task.</returns>
    [JSInvokable]
    public Task FacetTapped(string? facetId) => OnFacetTap.InvokeAsync(facetId);

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed || ReferenceEquals(mounted, View))
        {
            return Task.CompletedTask;
        }

        mounted = View;
        mounting = MountAsync(View, mounting);
        return mounting;
    }

    private static async Task DisposeModuleAsync(IJSObjectReference? reference)
    {
        if (reference is null)
        {
            return;
        }

        try
        {
            await reference.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Circuit already torn down.
        }
    }

    private static async Task DisposeViewerAsync(IJSObjectReference? handle)
    {
        if (handle is null)
        {
            return;
        }

        try
        {
            await handle.InvokeVoidAsync("dispose");
            await handle.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The browser side is already gone, and its WebGL context with it.
        }
    }

    private bool IsCurrent(Wall3DView view) => !disposed && ReferenceEquals(mounted, view);

    private async Task MountAsync(Wall3DView view, Task previous)
    {
        try
        {
            await previous;
        }
        catch (OperationCanceledException)
        {
            // The previous mount timed out; this one starts afresh.
        }

        if (!IsCurrent(view))
        {
            return;
        }

        await UnmountAsync();
        failure = null;
        try
        {
            module ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/wall3d.js");
            var handle = await module.InvokeAsync<IJSObjectReference>("mount", stage, view, MountOptions());
            if (!IsCurrent(view))
            {
                await DisposeViewerAsync(handle);
                return;
            }

            viewer = handle;
            await ListenFacetTapsAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit went away mid-mount; nothing to render into any more.
        }
        catch (JSException ex)
        {
            if (!IsCurrent(view))
            {
                return;
            }

            var model = ex.Message.Contains(ModelFailedPrefix, StringComparison.Ordinal);
            Logger.LogWarning(ex, "3D view of wall {WallId} failed to start ({Reason})", view.WallId, model ? "model" : "WebGL unavailable?");
            failure = model
                ? "This wall's 3D model could not be drawn."
                : "This device could not start the 3D view (WebGL is unavailable).";
            StateHasChanged();
        }
    }

    private Dictionary<string, object?> MountOptions() => new()
    {
        ["roleColors"] = RoleColors,
        ["initialPreset"] = InitialPreset,
        ["initialMode"] = InitialMode ?? "schematic",
        ["hintOnce"] = HintOnce,
    };

    private async Task ListenFacetTapsAsync()
    {
        if (!OnFacetTap.HasDelegate || viewer is null || disposed)
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
        await DisposeViewerAsync(current);
    }
}
