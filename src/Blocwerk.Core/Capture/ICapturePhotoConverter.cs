// <copyright file="ICapturePhotoConverter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>
/// Turns a capture photo the pipeline cannot read (HEIC/HEIF, the iPhone default) into a JPEG with its
/// pixels upright. The EXIF may still be in the result: the upload reads the camera facts from it and
/// strips it before anything is stored.
/// </summary>
public interface ICapturePhotoConverter
{
    /// <summary>The JPEG bytes. Throws <see cref="InvalidDataException"/> when the photo cannot be converted.</summary>
    Task<byte[]> ToJpegAsync(byte[] heic, CancellationToken ct);
}
