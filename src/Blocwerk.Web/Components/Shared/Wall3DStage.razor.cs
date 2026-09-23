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

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [Inject]
    private ILogger<Wall3DStage> Logger { get; set; } = null!;

    public async ValueTask DisposeAsync()
    {
        await UnmountAsync();
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
                });
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
