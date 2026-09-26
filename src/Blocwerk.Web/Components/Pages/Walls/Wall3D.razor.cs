// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The opt-in 3D view of a wall with a solved glyph geometry model: facets, markers and holds (as
/// their real outlines) in millimetres, rendered by <see cref="Shared.Wall3DStage"/>.
/// <c>?boulder={id}</c> highlights one boulder's holds; <c>?mode=schematic|photos|photoreal</c> picks
/// the start mode (schematic by default).
/// </summary>
public partial class Wall3D
{
    private Wall3DViewResult? _result;
    private (Guid Wall, string? Token, Guid? Boulder)? _loaded;

    [Parameter]
    public Guid WallId { get; set; }

    [Parameter]
    public string? ShareToken { get; set; }

    [SupplyParameterFromQuery(Name = "boulder")]
    public Guid? BoulderId { get; set; }

    [SupplyParameterFromQuery(Name = "mode")]
    public string? Mode { get; set; }

    [Inject]
    private IWall3DViewService ViewService { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

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

    // Route data is loaded here, not in OnInitializedAsync: enhanced navigation between two walls
    // (same route template) keeps this component alive and only swaps the parameters. The stage
    // remounts when the view instance changes.
    protected override async Task OnParametersSetAsync()
    {
        var key = (WallId, ShareToken, BoulderId);
        if (_loaded == key)
        {
            return;
        }

        _loaded = key;
        _result = null;
        try
        {
            _result = await ViewService.BuildAsync(WallId, BoulderId, ShareToken);
            await LoadCorrectionAsync();
        }
        catch (UnauthorizedAccessException)
        {
            // Same as the wall page: an anonymous visitor without a share link signs in first.
            Navigation.NavigateTo("/account/login", replace: true);
        }
    }

    private static string HoldCountText(Wall3DView view) => Wall3DHoldCountText.Format(view);

    private static string UnplacedText(int count) =>
        count == 1 ? "1 hold not measured yet" : $"{count} holds not measured yet";
}
