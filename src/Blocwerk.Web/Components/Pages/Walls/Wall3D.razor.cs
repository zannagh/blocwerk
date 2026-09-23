// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The opt-in 3D view of a wall with a solved glyph geometry model: facets, markers and holds in
/// millimetres, rendered by <c>wwwroot/js/wall3d.js</c> (three.js). <c>?boulder={id}</c> highlights
/// one boulder's holds.
/// </summary>
public partial class Wall3D : IAsyncDisposable
{
    private static readonly Dictionary<string, string> RoleColors = new()
    {
        ["Start"] = BoulderHoldColors.Start,
        ["Top"] = BoulderHoldColors.Top,
        ["Hand"] = BoulderHoldColors.Normal,
        ["Foot"] = BoulderHoldColors.Foot,
        ["ColorFoot"] = BoulderHoldColors.Foot,
    };

    private Wall3DViewResult? _result;
    private ElementReference _stage;
    private IJSObjectReference? _module;
    private IJSObjectReference? _viewer;
    private bool _needsMount;
    private bool _mountFailed;
    private (Guid Wall, string? Token, Guid? Boulder)? _loaded;

    [Parameter]
    public Guid WallId { get; set; }

    [Parameter]
    public string? ShareToken { get; set; }

    [SupplyParameterFromQuery(Name = "boulder")]
    public Guid? BoulderId { get; set; }

    [Inject]
    private IWall3DViewService ViewService { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [Inject]
    private ILogger<Wall3D> Logger { get; set; } = null!;

    private string SharedSuffix => string.IsNullOrEmpty(ShareToken) ? string.Empty : $"/shared/{Uri.EscapeDataString(ShareToken)}";

    private string BackUrl => BoulderId is { } b && _result?.View?.BoulderId == b
        ? $"/walls/{WallId}/boulders/{b}{SharedSuffix}"
        : $"/walls/{WallId}{SharedSuffix}";

    private string BackLabel => BoulderId is not null && _result?.View?.BoulderId is not null ? "Back to boulder" : "Back to wall";

    private string StatusMessage => _result?.Status switch
    {
        Wall3DViewStatus.NoGeometry => "This wall has no measured 3D model yet.",
        Wall3DViewStatus.InvalidGeometry => "This wall's 3D model could not be read.",
        Wall3DViewStatus.UnderMaintenance => "This wall is currently being updated. Please check back in a little while.",
        _ => "Wall not found.",
    };

    public async ValueTask DisposeAsync()
    {
        await UnmountAsync();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already torn down.
            }
        }

        GC.SuppressFinalize(this);
    }

    // Route data is loaded here, not in OnInitializedAsync: enhanced navigation between two walls
    // (same route template) keeps this component alive and only swaps the parameters.
    protected override async Task OnParametersSetAsync()
    {
        var key = (WallId, ShareToken, BoulderId);
        if (_loaded == key)
        {
            return;
        }

        _loaded = key;
        _result = null;
        _mountFailed = false;
        await UnmountAsync();
        try
        {
            _result = await ViewService.BuildAsync(WallId, BoulderId, ShareToken);
        }
        catch (UnauthorizedAccessException)
        {
            // Same as the wall page: an anonymous visitor without a share link signs in first.
            Navigation.NavigateTo("/account/login", replace: true);
            return;
        }

        _needsMount = _result.Status == Wall3DViewStatus.Ok;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_needsMount || _result?.View is not { } view)
        {
            return;
        }

        _needsMount = false;
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/wall3d.js");
            _viewer = await _module.InvokeAsync<IJSObjectReference>(
                "mount",
                _stage,
                view,
                new Dictionary<string, object> { ["roleColors"] = RoleColors, ["initialPreset"] = "front" });
        }
        catch (JSDisconnectedException)
        {
            // The circuit went away mid-mount; nothing to render into any more.
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "3D view of wall {WallId} failed to start (WebGL unavailable?)", WallId);
            _mountFailed = true;
            StateHasChanged();
        }
    }

    private static string UnplacedText(int count) =>
        count == 1 ? "1 hold not measured yet" : $"{count} holds not measured yet";

    private async Task UnmountAsync()
    {
        var viewer = _viewer;
        _viewer = null;
        if (viewer is null)
        {
            return;
        }

        try
        {
            await viewer.InvokeVoidAsync("dispose");
            await viewer.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The browser side is already gone, and its WebGL context with it.
        }
    }
}
