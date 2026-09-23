using System.Security.Cryptography;
using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Blocwerk.Core.Capture;

/// <summary>Photo upload: validate, read EXIF, strip metadata, store on disk, detect markers.</summary>
public sealed partial class WallCaptureService
{
    public async Task<CapturePhotoResult> AddPhotoAsync(Guid captureId, string? fileName, byte[] bytes, CancellationToken ct)
    {
        var name = fileName is { Length: > 256 } ? fileName[..256] : fileName;
        var kind = ValidateUpload(name, bytes);
        var (width, height) = RawSize(bytes, name);

        var (db, _, capture) = await OpenDraftAsync(captureId);
        await using (db)
        {
            var existing = await db.WallCapturePhotos.Where(p => p.CaptureId == captureId)
                .Select(p => new { p.Index, p.ContentHash }).ToListAsync(ct);
            if (existing.Count >= WallCapturePipelineOptions.MaxPhotos)
            {
                throw new InvalidOperationException($"A capture takes at most {WallCapturePipelineOptions.MaxPhotos} photos.");
            }

            // The stripper validates the structure first; EXIF is then read from the original, and what
            // is stored has none of it (no GPS on disk either).
            var clean = StripOrRefuse(bytes, name);
            var exif = ExifCameraReader.Read(bytes);
            var hash = Convert.ToHexStringLower(SHA256.HashData(clean));
            if (existing.Any(p => p.ContentHash == hash))
            {
                throw new InvalidOperationException($"{name ?? "This photo"} was already uploaded to this capture.");
            }

            // The draft's layout decides which ids are real: the plan's, or the legacy 0..35.
            var layout = await DraftLayoutAsync(db, capture);
            var markers = await CaptureMarkerDetection.DetectOrNullAsync(markerDetection, clean, layout.DetectionOptions, logger, ct);
            var photo = new WallCapturePhoto
            {
                CaptureId = capture.Id,
                Index = existing.Count == 0 ? 1 : existing.Max(p => p.Index) + 1,
                OriginalFileName = name,
                StoredPath = await files.SaveAsync(clean, CapturePhotoFormat.Extension(kind), ct),
                ContentType = CapturePhotoFormat.ContentType(kind),
                ContentHash = hash,
                SizeBytes = clean.LongLength,
                Width = width,
                Height = height,
                Focal35mm = exif.Focal35mm,
                CameraGroup = exif.CameraGroup(width, height),
                MarkersJson = markers is null ? null : JsonSerializer.Serialize(markers),
            };
            db.WallCapturePhotos.Add(photo);
            await db.SaveChangesAsync(ct);
            return ToResult(photo, Warnings(photo, markers));
        }
    }

    public async Task RemovePhotoAsync(Guid captureId, Guid photoId)
    {
        var (db, _, _) = await OpenDraftAsync(captureId);
        await using (db)
        {
            var photo = await db.WallCapturePhotos.FirstOrDefaultAsync(p => p.Id == photoId && p.CaptureId == captureId);
            if (photo is null)
            {
                return;
            }

            db.WallCapturePhotos.Remove(photo);
            await db.SaveChangesAsync();
            files.Delete(photo.StoredPath);
        }
    }

    private static CapturePhotoKind ValidateUpload(string? name, byte[] bytes)
    {
        var label = name ?? "The photo";
        if (bytes.LongLength > WallCapturePipelineOptions.MaxPhotoBytes)
        {
            throw new InvalidOperationException($"{label} is larger than {WallCapturePipelineOptions.MaxPhotoBytes / (1024 * 1024)} MB.");
        }

        return CapturePhotoFormat.Sniff(bytes) switch
        {
            CapturePhotoKind.Jpeg => CapturePhotoKind.Jpeg,
            CapturePhotoKind.Png => CapturePhotoKind.Png,
            CapturePhotoKind.Heic => throw new InvalidOperationException(
                $"{label} is a HEIC photo, which cannot be processed. Upload it from the Photos picker in the browser "
                + "(iOS converts it to JPEG), or set Camera → Formats → Most Compatible."),
            _ => throw new InvalidOperationException($"{label} is not a JPEG or PNG photo."),
        };
    }

    /// <summary>The RAW pixel grid (EXIF orientation ignored, like the marker decoder).</summary>
    private static (int Width, int Height) RawSize(byte[] bytes, string? name)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width < 16 || codec.Info.Height < 16)
        {
            throw new InvalidOperationException($"{name ?? "The photo"} could not be read as an image.");
        }

        if (ImagePixelLimit.IsTooLarge(codec.Info.Width, codec.Info.Height))
        {
            throw new InvalidOperationException(
                $"{name ?? "The photo"} has more than {ImagePixelLimit.MaxPixels / 1_000_000} megapixels. "
                + "Upload it at the camera's normal resolution.");
        }

        return (codec.Info.Width, codec.Info.Height);
    }

    private static byte[] StripOrRefuse(byte[] bytes, string? name)
    {
        try
        {
            return ImageMetadataStripper.Strip(bytes);
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException($"{name ?? "The photo"} is damaged or incomplete and cannot be used.");
        }
    }

    private static List<string> Warnings(WallCapturePhoto photo, IReadOnlyList<CaptureMarker>? markers)
    {
        var warnings = new List<string>();
        if (markers is { Count: 0 })
        {
            warnings.Add("No markers found — this photo will not be used.");
        }

        if (photo.Focal35mm is null)
        {
            warnings.Add("No focal length in the photo's EXIF — upload the camera original.");
        }

        return warnings;
    }
}
