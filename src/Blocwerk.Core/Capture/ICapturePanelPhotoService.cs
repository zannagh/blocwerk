using Blocwerk.Core.Services;

namespace Blocwerk.Core.Capture;

/// <summary>Where an admin wants one capture photo to go on the panel grid.</summary>
public sealed record CapturePanelAssignment(Guid PhotoId, int Col, int Row);

/// <summary>A capture photo ready for the full wall update: its name (for the uploader slot) and the staged photo.</summary>
public sealed record CaptureUpdatePhoto(string? FileName, BigUpdatePhoto Photo);

/// <summary>
/// Reuses capture photos as PANEL photos. A capture itself never touches panels; this is the only
/// bridge, and it adds no staging of its own: a new panel goes through
/// <see cref="IWallPanelService.StagePanelAsync"/> and re-photographed panels through the normal
/// full wall update (<see cref="IWallBigUpdateService.StageAsync"/>, started by the admin from the
/// pre-filled uploader). Every call is gated like the capture itself — wall admin, never a kiosk.
/// </summary>
public interface ICapturePanelPhotoService
{
    /// <summary>
    /// Stages the photo as a NEW panel at (<paramref name="col"/>,<paramref name="row"/>), which must be
    /// one of the grid's "+" cells (empty and next to a live panel) — the same rule the panel grid uses.
    /// </summary>
    Task<StagePanelResult> StageAsNewPanelAsync(Guid captureId, Guid photoId, int col, int row);

    /// <summary>
    /// Reads the chosen photos for a full wall update, in the order given. Refuses an empty choice,
    /// a photo used twice and two photos on one cell; the update's own rules (centre first) are
    /// enforced when it is staged.
    /// </summary>
    Task<IReadOnlyList<CaptureUpdatePhoto>> PrepareWallUpdateAsync(
        Guid captureId, IReadOnlyList<CapturePanelAssignment> assignments, CancellationToken ct);
}
