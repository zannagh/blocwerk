// <copyright file="HeifCapturePhotoConverter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>
/// <see cref="ICapturePhotoConverter"/> over libheif's <c>heif-convert</c> (Debian/Ubuntu package
/// <c>libheif-examples</c> plus <c>libheif-plugin-libde265</c>). It decodes the primary image, applies the
/// HEIF rotation/mirror to the pixels (so the JPEG is upright with orientation 1) and writes the image's
/// EXIF into the JPEG. The photo is written to a private temp folder that is deleted afterwards.
/// </summary>
public sealed class HeifCapturePhotoConverter(WallCapturePipelineOptions options) : ICapturePhotoConverter
{
    /// <summary>JPEG quality of the conversion: visually lossless for marker corners and textures.</summary>
    public const int Quality = 92;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    public async Task<byte[]> ToJpegAsync(byte[] heic, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory("blocwerk-capture-heic-");
        try
        {
            var input = Path.Combine(work.FullName, "photo.heic");
            var output = Path.Combine(work.FullName, "photo.jpg");
            await File.WriteAllBytesAsync(input, heic, ct);
            await CaptureToolProcess.RunAsync(options.HeifConvertPath, Arguments(input, output), Timeout, null, ct);
            var written = ConvertedFile(work, output)
                          ?? throw new InvalidDataException("heif-convert wrote no image.");
            return await File.ReadAllBytesAsync(written, ct);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    public static IReadOnlyList<string> Arguments(string input, string output) =>
        ["-q", Quality.ToString(System.Globalization.CultureInfo.InvariantCulture), input, output];

    /// <summary>
    /// The primary image: <c>photo.jpg</c>, or <c>photo-1.jpg</c> when the file held several top-level images
    /// (heif-convert then numbers them). Depth and auxiliary images are never taken.
    /// </summary>
    private static string? ConvertedFile(DirectoryInfo work, string output)
    {
        if (File.Exists(output))
        {
            return output;
        }

        var first = Path.Combine(work.FullName, "photo-1.jpg");
        return File.Exists(first) ? first : null;
    }
}
