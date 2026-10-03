// <copyright file="ComputePhotoParts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Compute;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Photo file parts of a compute job that the client streams from disk instead of holding every photo in memory: a
/// textures or splat request carries all capture photos (200 full-size 48 MP JPEGs are ~3–4 GB). Each photo is read
/// and metadata-stripped on its own (nothing leaves this server with GPS, camera serials or orientation); a stored
/// photo that is already clean (the upload stored it stripped) is streamed from the store as it is, any other one
/// from a stripped copy in a spool folder that <see cref="Dispose"/> deletes. Dispose only after the submission.
/// </summary>
public sealed class ComputePhotoParts(ICaptureFileStore files) : IDisposable
{
    private string? spoolDir;

    /// <summary>How many photos needed a stripped spool copy (the rest streamed straight from the store).</summary>
    public int Spooled { get; private set; }

    /// <summary>The part of one stored photo, or null when the file is missing.</summary>
    /// <param name="partName">The form field, e.g. <c>photos</c>.</param>
    /// <param name="stem">The file name without extension (the camera name the worker knows).</param>
    /// <param name="storedName">The photo's name in the capture store.</param>
    /// <param name="asJpeg">True for a video frame: always <c>.jpg</c> / <c>image/jpeg</c>, as ffmpeg wrote it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The part.</returns>
    public async Task<ComputeJobPart?> PartAsync(string partName, string stem, string storedName, bool asJpeg, CancellationToken ct)
    {
        var bytes = await files.ReadAsync(storedName, ct);
        if (bytes is null)
        {
            return null;
        }

        var clean = ImageMetadataStripper.Strip(bytes);
        var kind = CapturePhotoFormat.Sniff(clean);
        var fileName = stem + (asJpeg ? ".jpg" : CapturePhotoFormat.Extension(kind));
        var contentType = asJpeg ? "image/jpeg" : CapturePhotoFormat.ContentType(kind);
        if (files.ResolvePhysicalPath(storedName) is { } stored && clean.AsSpan().SequenceEqual(bytes))
        {
            return ComputeJobPart.FromDisk(partName, fileName, stored, contentType);
        }

        spoolDir ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "blocwerk-compute-parts", Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(spoolDir, $"{Spooled:D4}_{fileName}");
        await File.WriteAllBytesAsync(path, clean, ct);
        Spooled++;
        return ComputeJobPart.FromDisk(partName, fileName, path, contentType);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (spoolDir is null)
        {
            return;
        }

        try
        {
            Directory.Delete(spoolDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the OS temp folder is cleaned eventually.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        spoolDir = null;
    }
}
