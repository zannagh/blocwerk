using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// A finished capture's photos, opened on demand from its history row so any of them can be reused
/// as a panel photo. Opening them only reads; the capture is never changed.
/// </summary>
public partial class WallCaptureStatusList
{
    private Guid? openCaptureId;
    private IReadOnlyList<CapturePhotoResult>? openPhotos;

    /// <summary>Raised with the panel id when a capture photo was staged as a new panel.</summary>
    [Parameter]
    public EventCallback<Guid> OnPanelStaged { get; set; }

    /// <summary>Raised with the chosen capture photos to start a full wall update with.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<CaptureUpdatePhoto>> OnStartWallUpdate { get; set; }

    private async Task TogglePhotosAsync(Guid captureId)
    {
        if (openCaptureId == captureId)
        {
            openCaptureId = null;
            openPhotos = null;
            return;
        }

        try
        {
            openPhotos = await Captures.GetPhotosAsync(captureId);
            openCaptureId = captureId;
            failure = null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load the photos of capture {CaptureId}", captureId);
            failure = "The photos of that capture could not be loaded.";
        }
    }
}
