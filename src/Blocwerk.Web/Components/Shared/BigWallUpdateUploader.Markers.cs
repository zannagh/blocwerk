using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The marker declaration every wall update makes, defaulting to the wall's current setting. See
/// <see cref="WallMarkerDeclarationState"/>: a changed answer is saved before <c>OnStart</c> hands the
/// photos to <see cref="IWallBigUpdateService.StageAsync"/>, whose signature is unchanged.
/// </summary>
public partial class BigWallUpdateUploader
{
    private readonly WallMarkerDeclarationState markers = new();

    /// <summary>The wall being updated; its marker setting is the default for this update's declaration.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    [Inject]
    private IWallGlyphService GlyphService { get; set; } = default!;

    [Inject]
    private ILogger<BigWallUpdateUploader> Logger { get; set; } = default!;

    protected override Task OnParametersSetAsync()
    {
        ApplyPrefill();
        return markers.LoadAsync(GlyphService, WallId, Logger);
    }

    /// <summary>Saves a changed declaration; false (with the error shown) stops the update from starting.</summary>
    private async Task<bool> DeclareMarkersAsync()
    {
        _error = await markers.ApplyAsync(GlyphService, WallId, Logger);
        return _error is null;
    }
}
