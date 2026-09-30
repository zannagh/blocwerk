// <copyright file="CapturePhotoSharpness.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The stored sharpness of a capture photo (<see cref="WallCapturePhoto.Sharpness"/>). A real photo never scores exactly 0
/// (<see cref="CaptureFrameSharpness.Score"/> answers 0 only when it cannot decode it), and before the scorer decoded
/// colour JPEGs every photo was stored with 0: so 0, like null, means "not scored" and is scored again where the score
/// is needed.
/// </summary>
public static class CapturePhotoSharpness
{
    /// <summary>Whether <paramref name="sharpness"/> is a real score (not null, not the bogus or undecodable 0).</summary>
    public static bool IsScored(double? sharpness) => sharpness is > 0;

    /// <summary>The value to store for a fresh <paramref name="score"/>: null when the photo could not be scored.</summary>
    public static double? Stored(double score) => score > 0 ? score : null;

    /// <summary>
    /// Scores <paramref name="photo"/> from its stored file when it has no real score yet, off the calling thread; the row
    /// is changed in place. Returns whether it got a real score (false when it had one, or its file is gone or undecodable).
    /// </summary>
    public static async Task<bool> ScoreIfMissingAsync(WallCapturePhoto photo, ICaptureFileStore files, int edge, CancellationToken ct)
    {
        if (IsScored(photo.Sharpness))
        {
            return false;
        }

        var bytes = await files.ReadAsync(photo.StoredPath, ct);
        if (bytes is null)
        {
            return false;
        }

        photo.Sharpness = Stored(await Task.Run(() => CaptureFrameSharpness.Score(bytes, edge), ct));
        return photo.Sharpness is not null;
    }
}
