// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>Segments, the per-frame timestamp, pose and intrinsics, and the depth samples.</summary>
public static partial class ScanManifestValidator
{
    private static readonly HashSet<string> TrackingStates = new(StringComparer.Ordinal) { "normal", "limited", "notAvailable" };

    private static Dictionary<int, (double Start, double End)> CheckSegments(
        IReadOnlyList<ScanSegment>? segments, Dictionary<string, ScanFileEntry> files, ScanManifestErrors errors)
    {
        var valid = new Dictionary<int, (double, double)>();
        if (segments is not { Count: > 0 } || segments.Count > ScanPackageLimits.MaxSegments)
        {
            errors.Add($"segments must hold between 1 and {ScanPackageLimits.MaxSegments} segments.");
            return valid;
        }

        foreach (var s in segments)
        {
            if (s.Id is not { } id || s.StartTime is not { } start || s.EndTime is not { } end || !double.IsFinite(start)
                || !double.IsFinite(end) || end <= start || end - start > ScanPackageLimits.MaxDurationSeconds || !valid.TryAdd(id, (start, end)))
            {
                errors.Add($"segment {s.Id}: needs a unique id and startTime < endTime within {ScanPackageLimits.MaxDurationSeconds / 60} minutes.");
                continue;
            }

            if (s.Videos is not { Count: > 0 })
            {
                errors.Add($"segment {id}: videos is empty; every segment is recorded as 4K video.");
            }

            foreach (var piece in s.Videos ?? [])
            {
                CheckVideoPiece(id, piece, start, end, files, errors);
            }
        }

        return valid;
    }

    private static void CheckVideoPiece(
        int segment, ScanVideoPiece piece, double start, double end, Dictionary<string, ScanFileEntry> files, ScanManifestErrors errors)
    {
        if (ScanFileNames.KindOf(piece.File) != ScanFileKind.Video)
        {
            errors.Add($"segment {segment}: video \"{piece.File}\" is not a video/*.mov|mp4 name.");
            return;
        }

        RequireListed(piece.File!, files, $"segment {segment} video", errors);
        if (piece.StartTime is not { } t || t < start - Eps || t > end + Eps)
        {
            errors.Add($"segment {segment}: {piece.File} starts at {piece.StartTime}, outside the segment.");
        }
    }

    private static void CheckFrames(IReadOnlyList<ScanFrame>? frames, Dictionary<int, (double Start, double End)> segments, ScanManifestErrors errors)
    {
        if (frames is not { Count: > 0 } || frames.Count > ScanPackageLimits.MaxFrames)
        {
            errors.Add($"frames must hold between 1 and {ScanPackageLimits.MaxFrames} frames.");
            return;
        }

        var last = new Dictionary<int, double>();
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (!InSegment(f.T, f.Segment, segments))
            {
                errors.Add($"frames[{i}]: t {f.T} is not inside segment {f.Segment}.");
            }
            else if (last.TryGetValue(f.Segment!.Value, out var previous) && f.T <= previous)
            {
                errors.Add($"frames[{i}]: t {f.T} does not increase within segment {f.Segment}.");
            }
            else
            {
                last[f.Segment.Value] = f.T!.Value;
            }

            CheckFrame(i, f, errors);
        }
    }

    private static void CheckFrame(int i, ScanFrame f, ScanManifestErrors errors)
    {
        if (ScanPoseCheck.Problem(f.Pose) is { } pose)
        {
            errors.Add($"frames[{i}].pose: {pose}");
        }

        if (ScanPoseCheck.IntrinsicsProblem(f.Intrinsics, ScanPackageLimits.VideoWidth, ScanPackageLimits.VideoHeight) is { } k)
        {
            errors.Add($"frames[{i}].intrinsics: {k}");
        }

        if (f.Tracking is null || !TrackingStates.Contains(f.Tracking))
        {
            errors.Add($"frames[{i}].tracking must be normal, limited or notAvailable.");
        }
    }

    private static void CheckDepth(
        IReadOnlyList<ScanDepthFrame>? depth, int frameCount, Dictionary<int, (double Start, double End)> segments,
        Dictionary<string, ScanFileEntry> files, ScanManifestErrors errors)
    {
        if (depth is not { Count: > 0 } || depth.Count > Math.Min(ScanPackageLimits.MaxDepthFrames, Math.Max(frameCount, 1)))
        {
            errors.Add("depth must hold at least one sample and no more than there are frames.");
            return;
        }

        for (var i = 0; i < depth.Count; i++)
        {
            var d = depth[i];
            if (!InSegment(d.T, d.Segment, segments))
            {
                errors.Add($"depth[{i}]: t {d.T} is not inside segment {d.Segment}.");
            }

            if (d.Width is not (> 0 and <= ScanPackageLimits.MaxDepthSide) || d.Height is not (> 0 and <= ScanPackageLimits.MaxDepthSide))
            {
                errors.Add($"depth[{i}]: width and height must be 1–{ScanPackageLimits.MaxDepthSide} pixels.");
            }

            CheckDepthFile(i, d.File, ScanFileKind.Depth, "file", files, errors);
            CheckDepthFile(i, d.ConfidenceFile, ScanFileKind.Confidence, "confidenceFile", files, errors);
        }
    }

    private static void CheckDepthFile(
        int i, string? name, ScanFileKind kind, string field, Dictionary<string, ScanFileEntry> files, ScanManifestErrors errors)
    {
        if (ScanFileNames.KindOf(name) != kind)
        {
            errors.Add($"depth[{i}].{field} \"{name}\" is not a {kind.ToString().ToLowerInvariant()}/*.png name.");
            return;
        }

        RequireListed(name!, files, $"depth[{i}].{field}", errors);
    }

    private static bool InSegment(double? t, int? segment, Dictionary<int, (double Start, double End)> segments) =>
        t is { } time && segment is { } id && segments.TryGetValue(id, out var range)
        && time >= range.Start - Eps && time <= range.End + Eps;
}
