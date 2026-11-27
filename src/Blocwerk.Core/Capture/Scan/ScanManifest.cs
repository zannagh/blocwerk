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

    public Guid? WallId { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public ScanApp? App { get; init; }

    public ScanDevice? Device { get; init; }

    public ScanVideoFormat? Video { get; init; }

    public ScanExposureLock? ExposureLock { get; init; }

    /// <summary>Three points in ARKit world (metres): the two floor ends of the wall and a point on its top edge.</summary>
    public IReadOnlyList<double[]>? WallBox { get; init; }

    public IReadOnlyList<ScanSegment>? Segments { get; init; }

    /// <summary>Every recorded video frame, in time order per segment.</summary>
    public IReadOnlyList<ScanFrame>? Frames { get; init; }

    /// <summary>LiDAR depth samples, at a lower rate than the frames.</summary>
    public IReadOnlyList<ScanDepthFrame>? Depth { get; init; }

    public ScanMesh? Mesh { get; init; }

    public IReadOnlyList<ScanFileEntry>? Files { get; init; }
}
