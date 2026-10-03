// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The photos' sharpness travels with the package: the export scores the photos that have no real score yet (null, or the
/// bogus 0 stored before the scorer read colour JPEGs; <see cref="CapturePhotoSharpness"/>), and the import scores those
/// a package from an older source still carries, from the files it just moved into the store.
/// </summary>
public sealed partial class CapturePackageService
{
    private int PhotoSharpnessEdge => (pipelineOptions ?? new WallCapturePipelineOptions()).PhotoSharpnessEdge;

    /// <summary>Scores the unscored <paramref name="photos"/> in place (the rows are the package's, not tracked).</summary>
    private async Task ScoreUnscoredPhotosAsync(IEnumerable<WallCapturePhoto> photos, CancellationToken ct)
    {
        foreach (var photo in photos.Where(p => !CapturePhotoSharpness.IsScored(p.Sharpness)))
        {
            await CapturePhotoSharpness.ScoreIfMissingAsync(photo, files, PhotoSharpnessEdge, ct);
        }
    }
}
