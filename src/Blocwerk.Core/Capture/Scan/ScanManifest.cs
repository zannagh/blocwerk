// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// <c>manifest.json</c> of an iPhone scan package, schema <c>blocwerk-scan/1</c> (docs/scan-package.md). Every member is
/// nullable so a missing one reaches <see cref="ScanManifestValidator"/> and is reported by name instead of failing the
/// JSON read. ARKit conventions throughout: world right-handed, Y up = gravity, metres; times in seconds on the
/// session clock (<c>ARFrame.timestamp</c>).
/// </summary>
public sealed record ScanManifest
{
    public string? SchemaVersion { get; init; }

    public Guid? ScanId { get; init; }

    /// <summary>Optional: the wall comes from the URL and is stored from there; when sent, it must be that wall.</summary>
    public Guid? WallId { get; init; }

    /// <summary>A generated test package, not a real recording; stored on the scan and shown as "synthetic".</summary>
    public bool? Synthetic { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public ScanApp? App { get; init; }

    public ScanDevice? Device { get; init; }

    public ScanVideoFormat? Video { get; init; }

    /// <summary>
    /// The lock of the whole recording. Kept for older apps: a package needs either this or an
    /// <see cref="ScanSegment.ExposureLock"/> on every segment.
    /// </summary>
    public ScanExposureLock? ExposureLock { get; init; }

    /// <summary>
    /// Optional: three points in ARKit world (metres), the two floor ends of the wall and a point on its top edge. Older
    /// apps send it; the app now sends <see cref="ReferencePlane"/> instead.
    /// </summary>
    public IReadOnlyList<double[]>? WallBox { get; init; }

    /// <summary>Optional: the wall's plane, a point on it and its normal (ARKit world, metres).</summary>
    public ScanReferencePlane? ReferencePlane { get; init; }

    /// <summary>Optional: how far the wall reaches in front of and behind its plane (overhangs).</summary>
    public ScanWallBoxReach? WallBoxReach { get; init; }

    /// <summary>Optional: how much of the wall the app saw well enough, as it showed the climber while scanning.</summary>
    public ScanCoverage? Coverage { get; init; }

    public IReadOnlyList<ScanSegment>? Segments { get; init; }

    /// <summary>Every recorded video frame, in time order per segment.</summary>
    public IReadOnlyList<ScanFrame>? Frames { get; init; }

    /// <summary>LiDAR depth samples, at a lower rate than the frames.</summary>
    public IReadOnlyList<ScanDepthFrame>? Depth { get; init; }

    public ScanMesh? Mesh { get; init; }

    public IReadOnlyList<ScanFileEntry>? Files { get; init; }
}
