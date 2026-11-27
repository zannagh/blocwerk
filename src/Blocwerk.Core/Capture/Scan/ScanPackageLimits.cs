// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// The ceilings of one scan package. A 4 × 3 m wall recorded for about four minutes is roughly 0.8 GB (design §2); the
/// app stops at ten minutes, so these leave room for that and nothing much beyond.
/// </summary>
public static class ScanPackageLimits
{
    /// <summary>The only schema this server reads.</summary>
    public const string SchemaV1 = "blocwerk-scan/1";

    /// <summary>
    /// The manifest carries one record per video frame, about 435 bytes each as the app writes them. The app's ten
    /// minutes at 30 fps (18 000 frames) with as many depth maps as the file cap allows come to about 11 MB, so this
    /// leaves room for that. A begin reads the whole manifest into memory, so it stays small.
    /// </summary>
    public const long MaxManifestBytes = 16L * 1024 * 1024;

    public const int MaxFiles = 20_000;

    public const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>One HEVC piece (about 60 s of 4K).</summary>
    public const long MaxVideoFileBytes = 1024L * 1024 * 1024;

    /// <summary>A depth or confidence PNG (256 × 192 at 16 bit is under 100 kB raw).</summary>
    public const long MaxDepthFileBytes = 2L * 1024 * 1024;

    public const long MaxMeshBytes = 512L * 1024 * 1024;

    /// <summary>Ten minutes at 60 frames per second.</summary>
    public const int MaxFrames = 36_000;

    public const int MaxDepthFrames = 36_000;

    public const int MaxSegments = 64;

    /// <summary>The longest a scan may span on the session clock (the app stops at ten minutes; pauses add some).</summary>
    public const double MaxDurationSeconds = 30 * 60;

    /// <summary>The smallest video frame taken: 1280×720 (16:9) or 1280×960 (4:3).</summary>
    public const int MinVideoWidth = 1280;

    public const int MinVideoHeight = 720;

    /// <summary>The largest video frame taken: 3840×2160 (16:9) or 2880×2160 (4:3).</summary>
    public const int MaxVideoWidth = 3840;

    public const int MaxVideoHeight = 2160;

    /// <summary>The most named bins a coverage summary may carry.</summary>
    public const int MaxCoverageBins = 32;

    /// <summary>The furthest a wall box reaches in front of or behind its plane (overhangs, a roof), in metres.</summary>
    public const double MaxWallBoxReachMeters = 20;

    public const int MaxDepthSide = 1024;

    /// <summary>The cap of one file by its folder in the package.</summary>
    public static long MaxBytesOf(string name) => ScanFileNames.KindOf(name) switch
    {
        ScanFileKind.Video => MaxVideoFileBytes,
        ScanFileKind.Mesh => MaxMeshBytes,
        _ => MaxDepthFileBytes,
    };
}
