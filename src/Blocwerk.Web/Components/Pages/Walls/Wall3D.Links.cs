// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The cross-panel link hint on the 3D page: shown only once the viewer is known to be a wall admin (checked after the
/// first interactive render, never behind a share link), so other viewers cause no suggestion queries. A link made
/// there is drawn after "Update view", which rebuilds the view without reloading the whole page.
/// </summary>
public partial class Wall3D
{
    private Guid linksCheckedFor;
    private bool canReviewLinks;
    private bool linksChanged;

    [Inject]
    private IHoldLinkSuggestionService LinkSuggestions { get; set; } = null!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (linksCheckedFor == WallId || !string.IsNullOrEmpty(ShareToken) || _result?.Status != Wall3DViewStatus.Ok)
        {
            return;
        }

        linksCheckedFor = WallId;
        (canReviewLinks, linksChanged) = (await LinkSuggestions.CanReviewAsync(WallId), false);
        if (canReviewLinks)
        {
            StateHasChanged();
        }
    }

    private void LinksLinked() => linksChanged = true;

    private async Task UpdateViewAsync()
    {
        linksChanged = false;
        try
        {
            _result = await ViewService.BuildAsync(WallId, BoulderId, ShareToken);
            await LoadCorrectionAsync();
        }
        catch (UnauthorizedAccessException)
        {
            Navigation.NavigateTo("/account/login", replace: true);
        }
    }
}
