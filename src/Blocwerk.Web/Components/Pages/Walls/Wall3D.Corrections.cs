// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The 3D view's correction mode (<c>?correct=1</c>): a wall admin taps a surface and declares it vertical or drops it
/// (<see cref="IWallGeometryCorrectionService"/>). Never on a share link; a member who is not an admin sees the plain view.
/// </summary>
public partial class Wall3D
{
    private GeometryCorrectionState? correction;
    private GeometryCorrectionFacet? tappedFacet;
    private bool correcting;
    private string? correctionMessage;

    /// <summary>"1" (or "true") turns the correction mode on.</summary>
    [SupplyParameterFromQuery(Name = "correct")]
    public string? Correct { get; set; }

    [Inject]
    private IWallGeometryCorrectionService Corrections { get; set; } = null!;

    // Only in correction mode does the stage report surface taps.
    private EventCallback<string?> FacetTap =>
        correction is null ? default : EventCallback.Factory.Create<string?>(this, FacetTapped);

    private async Task LoadCorrectionAsync()
    {
        correction = null;
        tappedFacet = null;
        if (Correct is not ("1" or "true") || !string.IsNullOrEmpty(ShareToken) || _result?.Status != Wall3DViewStatus.Ok)
        {
            return;
        }

        try
        {
            correction = await Corrections.GetStateAsync(WallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            correction = null;
        }
    }

    private void FacetTapped(string? facetId)
    {
        tappedFacet = facetId is null ? null : correction?.Facets.FirstOrDefault(f => f.Id == facetId);
        correctionMessage = null;
    }

    private Task VerticalAsync(string facetId) => CorrectAsync(() => Corrections.SetVerticalSurfaceAsync(WallId, facetId));

    private Task DropAsync(string facetId) => CorrectAsync(() => Corrections.DropSurfaceAsync(WallId, facetId));

    /// <summary>Runs a correction, then rebuilds the view on the new model version (the stage remounts).</summary>
    private async Task CorrectAsync(Func<Task<GeometryCorrectionResult>> run)
    {
        if (correcting)
        {
            return;
        }

        correcting = true;
        try
        {
            var result = await run();
            _result = await ViewService.BuildAsync(WallId, BoulderId, ShareToken);
            await LoadCorrectionAsync();
            correctionMessage = $"{result.Summary}. Saved as a new model version; the previous one stays in the wall's model history.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            correctionMessage = ex.Message;
        }
        finally
        {
            correcting = false;
        }
    }
}
