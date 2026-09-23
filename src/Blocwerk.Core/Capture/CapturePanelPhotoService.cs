using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// <see cref="ICapturePanelPhotoService"/>. The photo is read through
/// <see cref="IWallCaptureService.ReadPhotoAsync"/> first, so the capture's own gate (wall admin, not a
/// kiosk, wall taken from the capture row) runs before any panel service is called.
/// </summary>
public sealed class CapturePanelPhotoService(
    IWallCaptureService captures,
    IWallPanelService panels,
    ILogger<CapturePanelPhotoService> logger) : ICapturePanelPhotoService
{
    public async Task<StagePanelResult> StageAsNewPanelAsync(Guid captureId, Guid photoId, int col, int row)
    {
        var photo = await captures.ReadPhotoAsync(captureId, photoId, CancellationToken.None);
        var frontier = await panels.GetFrontierPositionsAsync(photo.WallId);
        if (!frontier.Any(p => p.Col == col && p.Row == row))
        {
            throw new InvalidOperationException(
                $"({col},{row}) is not an empty cell next to a live panel, so a new panel cannot go there.");
        }

        var result = await panels.StagePanelAsync(photo.WallId, col, row, photo.Bytes, photo.ContentType);
        logger.LogInformation(
            "Capture {CaptureId} photo {PhotoId} staged as new panel {PanelId} at ({Col},{Row}) on wall {WallId}",
            captureId, photoId, result.PanelId, col, row, photo.WallId);
        return result;
    }

    public async Task<IReadOnlyList<CaptureUpdatePhoto>> PrepareWallUpdateAsync(
        Guid captureId, IReadOnlyList<CapturePanelAssignment> assignments, CancellationToken ct)
    {
        if (assignments.Count == 0)
        {
            throw new InvalidOperationException("Pick at least one photo for the wall update.");
        }

        if (assignments.Select(a => a.PhotoId).Distinct().Count() != assignments.Count)
        {
            throw new InvalidOperationException("A photo can only go to one panel.");
        }

        if (assignments.Select(a => (a.Col, a.Row)).Distinct().Count() != assignments.Count)
        {
            throw new InvalidOperationException("Two photos are aimed at the same panel.");
        }

        var result = new List<CaptureUpdatePhoto>(assignments.Count);
        foreach (var assignment in assignments)
        {
            var photo = await captures.ReadPhotoAsync(captureId, assignment.PhotoId, ct);
            result.Add(new CaptureUpdatePhoto(
                photo.FileName, new BigUpdatePhoto(photo.Bytes, photo.ContentType, assignment.Col, assignment.Row)));
        }

        return result;
    }
}
