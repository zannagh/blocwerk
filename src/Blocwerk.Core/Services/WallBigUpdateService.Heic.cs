// <copyright file="WallBigUpdateService.Heic.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Staged photos arrive as the browser sent them: an iPhone HEIC under the uploader's size limit is not
/// converted there. Hold detection, the matcher and the browser all need JPEG, so a HEIC is converted
/// here exactly as a capture upload is (<see cref="ICapturePhotoConverter"/>: pixels turned upright), and
/// its metadata is then dropped entirely — the pixels are already upright, so an orientation tag carried
/// over from the HEIC would turn the photo a second time.
/// </summary>
public partial class WallBigUpdateService
{
    private async Task<IReadOnlyList<BigUpdatePhoto>> PrepareStagedPhotosAsync(IReadOnlyList<BigUpdatePhoto> photos)
    {
        var prepared = new List<BigUpdatePhoto>(photos.Count);
        foreach (var photo in photos)
        {
            if (CapturePhotoFormat.Sniff(photo.Image) != CapturePhotoKind.Heic)
            {
                prepared.Add(photo with { Image = StoredPhotoSanitizer.Sanitize(photo.Image) });
                continue;
            }

            var jpeg = await ConvertStagedHeicAsync(photo);
            prepared.Add(photo with { Image = ImageMetadataStripper.Strip(jpeg, keepColour: true), ContentType = "image/jpeg" });
        }

        return prepared;
    }

    private async Task<byte[]> ConvertStagedHeicAsync(BigUpdatePhoto photo)
    {
        const string Refusal = "is a HEIC photo that could not be converted. Export it as JPEG and upload that.";
        if (photoConverter is null)
        {
            throw new InvalidOperationException($"The photo for panel ({photo.Col},{photo.Row}) {Refusal}");
        }

        try
        {
            var jpeg = await photoConverter.ToJpegAsync(photo.Image, CancellationToken.None);
            if (CapturePhotoFormat.Sniff(jpeg) == CapturePhotoKind.Jpeg)
            {
                return jpeg;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "HEIC conversion failed for the staged photo of panel ({Col},{Row})", photo.Col, photo.Row);
        }

        throw new InvalidOperationException($"The photo for panel ({photo.Col},{photo.Row}) {Refusal}");
    }
}
