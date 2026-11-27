// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>The video format and size, the exposure locks, the per-frame camera settings, the wall box reach and the coverage.</summary>
public static partial class ScanManifestValidator
{
    /// <summary>Checks codec, size and rate; answers the size the frames' intrinsics are in (the largest when it is bad).</summary>
    private static (int Width, int Height) CheckVideoFormat(ScanVideoFormat? video, ScanManifestErrors errors)
    {
        var largest = (ScanPackageLimits.MaxVideoWidth, ScanPackageLimits.MaxVideoHeight);
        if (video is null)
        {
            errors.Add("video is missing.");
            return largest;
        }

        if (!string.Equals(video.Codec, "hevc", StringComparison.Ordinal))
        {
            errors.Add($"video.codec must be \"hevc\", not \"{video.Codec}\".");
        }

        if (video.Fps is not (>= 1 and <= 60))
        {
            errors.Add($"video.fps must be between 1 and 60, not {video.Fps}.");
        }

        if (video is not { Width: { } w, Height: { } h } || !IsVideoSize(w, h))
        {
            errors.Add($"video must be 16:9 or 4:3 between {ScanPackageLimits.MinVideoWidth}×{ScanPackageLimits.MinVideoHeight} and "
                       + $"{ScanPackageLimits.MaxVideoWidth}×{ScanPackageLimits.MaxVideoHeight}, not {video.Width}×{video.Height}.");
            return largest;
        }

        return (w, h);
    }

    /// <summary>Within the bounds and 16:9 or 4:3, to a pixel (encoders round odd sizes).</summary>
    private static bool IsVideoSize(int w, int h) =>
        w is >= ScanPackageLimits.MinVideoWidth and <= ScanPackageLimits.MaxVideoWidth
        && h is >= ScanPackageLimits.MinVideoHeight and <= ScanPackageLimits.MaxVideoHeight
        && (Math.Abs((w * 9) - (h * 16)) <= 16 || Math.Abs((w * 3) - (h * 4)) <= 4);

    /// <summary>The recording's lock, or one per segment; at least one of the two, and every lock sent is checked.</summary>
    private static void CheckExposureLocks(ScanManifest m, ScanManifestErrors errors)
    {
        var segments = m.Segments ?? [];
        if (m.ExposureLock is null && (segments.Count == 0 || segments.Any(s => s.ExposureLock is null)))
        {
            errors.Add("exposureLock is missing: send it for the recording or on every segment (segments[].exposureLock).");
        }

        if (m.ExposureLock is { } top)
        {
            CheckExposureLock("exposureLock", top, errors);
        }

        foreach (var s in segments.Where(s => s.ExposureLock is not null))
        {
            CheckExposureLock($"segment {s.Id} exposureLock", s.ExposureLock!, errors);
        }
    }

    private static void CheckExposureLock(string field, ScanExposureLock lk, ScanManifestErrors errors)
    {
        if (lk.ExposureDurationSeconds is not (> 0 and < 1) || lk.Iso is not (> 0 and < 100_000))
        {
            errors.Add($"{field}: exposureDurationSeconds (0–1 s) and iso (> 0) are required.");
        }

        if (!IsGain(lk.WhiteBalanceRed) || !IsGain(lk.WhiteBalanceGreen) || !IsGain(lk.WhiteBalanceBlue))
        {
            errors.Add($"{field}: whiteBalanceRed/Green/Blue must be device gains between 1 and 16.");
        }

        if (lk.LensPosition is not (>= 0 and <= 1) || lk.LockedAt is not { } at || !double.IsFinite(at))
        {
            errors.Add($"{field}: lensPosition (0–1) and lockedAt (session time) are required.");
        }
    }

    /// <summary>A frame's own settings, when the app sends them: the same ranges as the lock.</summary>
    private static string? CameraProblem(ScanFrameCamera c)
    {
        if (c.LensPosition is not (>= 0 and <= 1) || c.Iso is not (> 0 and < 100_000) || c.ExposureDurationSeconds is not (> 0 and < 1))
        {
            return "lensPosition (0–1), iso (> 0) and exposureDurationSeconds (0–1 s) are required.";
        }

        return c.WhiteBalanceGains is { Length: 3 } gains && gains.All(g => IsGain(g))
            ? null
            : "whiteBalanceGains must be three device gains (red, green, blue) between 1 and 16.";
    }

    private static bool IsGain(double? gain) => gain is >= 1 and <= 16;

    private static void CheckWallBoxReach(ScanWallBoxReach? reach, ScanManifestErrors errors)
    {
        if (reach is not null && (!IsReach(reach.FrontMeters) || !IsReach(reach.BackMeters)))
        {
            errors.Add($"wallBoxReach.frontMeters and backMeters must be 0–{ScanPackageLimits.MaxWallBoxReachMeters} m.");
        }
    }

    private static bool IsReach(double? meters) => meters is >= 0 and <= ScanPackageLimits.MaxWallBoxReachMeters;

    private static void CheckCoverage(ScanCoverage? coverage, ScanManifestErrors errors)
    {
        if (coverage is null)
        {
            return;
        }

        if (coverage.PercentGreen is not (>= 0 and <= 100) || coverage.PatchSizeMeters is not (> 0 and <= 10))
        {
            errors.Add("coverage.percentGreen (0–100) and coverage.patchSizeMeters (> 0, at most 10 m) are required.");
        }

        var bins = coverage.Bins ?? new Dictionary<string, int>();
        if (bins.Count > ScanPackageLimits.MaxCoverageBins || bins.Any(b => b.Key.Length is 0 or > 64 || b.Value < 0))
        {
            errors.Add($"coverage.bins: at most {ScanPackageLimits.MaxCoverageBins} named bins (names up to 64 characters), each a patch count of 0 or more.");
        }
    }
}
