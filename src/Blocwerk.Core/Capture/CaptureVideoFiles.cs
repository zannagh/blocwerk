// <copyright file="CaptureVideoFiles.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>The stored files a capture's walk-along video owns: the video itself and its extracted frames.</summary>
public static class CaptureVideoFiles
{
    /// <summary>Accepted video file extensions (MP4 / QuickTime containers).</summary>
    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };

    /// <summary>A video longer than this is refused on upload: a 30–90 s walk is plenty.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);

    /// <summary>The video (if still there) and every extracted frame, as bare store names.</summary>
    public static IEnumerable<string> Of(WallCapture capture) => Of(capture.VideoStoredPath, capture.VideoFramesJson);

    public static IEnumerable<string> Of(string? videoStoredPath, string? framesJson)
    {
        if (videoStoredPath is not null)
        {
            yield return videoStoredPath;
        }

        foreach (var frame in Frames(framesJson))
        {
            yield return frame;
        }
    }

    /// <summary>The stored frame names, in video order (empty for null or malformed JSON).</summary>
    public static IReadOnlyList<string> Frames(string? framesJson)
    {
        if (string.IsNullOrWhiteSpace(framesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(framesJson)?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string FramesJson(IEnumerable<string> names) => JsonSerializer.Serialize(names.ToList());

    /// <summary>True when the file starts like an ISO base-media (MP4/MOV) file: a box whose type is <c>ftyp</c>.</summary>
    public static bool LooksLikeIsoMedia(ReadOnlySpan<byte> head) =>
        head.Length >= 12 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p';
}
