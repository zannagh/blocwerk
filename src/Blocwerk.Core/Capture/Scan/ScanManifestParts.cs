// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>The companion app that wrote the package.</summary>
public sealed record ScanApp(string? Version, string? Build);

/// <summary>The phone: model identifier (e.g. <c>iPhone17,1</c>) and iOS version.</summary>
public sealed record ScanDevice(string? Model, string? OsVersion);

/// <summary>The recording's video format: 4K HEVC only (<c>3840×2160</c>, codec <c>hevc</c>).</summary>
public sealed record ScanVideoFormat(string? Codec, int? Width, int? Height, double? Fps);

/// <summary>
/// The camera settings locked when the recording started (<c>configurableCaptureDeviceForPrimaryCamera</c>), so every
/// frame shares exposure, white balance and focus.
/// </summary>
public sealed record ScanExposureLock(
    double? ExposureDurationSeconds,
    double? Iso,
    double? WhiteBalanceRed,
    double? WhiteBalanceGreen,
    double? WhiteBalanceBlue,
    double? LensPosition,
    double? LockedAt);

/// <summary>A tracking segment (its own ARKit world frame after a failed relocalization) and its video pieces.</summary>
public sealed record ScanSegment(int? Id, double? StartTime, double? EndTime, IReadOnlyList<ScanVideoPiece>? Videos);

/// <summary>One HEVC piece of a segment's recording (about 60 s), starting at <see cref="StartTime"/>.</summary>
public sealed record ScanVideoPiece(string? File, double? StartTime);

/// <summary>
/// One video frame: timestamp, segment, camera-to-world <see cref="Pose"/> (16 numbers, column-major, ARKit camera
/// x right, y up, looking down −Z), intrinsics at the video resolution and the tracking state.
/// </summary>
public sealed record ScanFrame(double? T, int? Segment, double[]? Pose, ScanIntrinsics? Intrinsics, string? Tracking);

/// <summary>Pinhole intrinsics in pixels of the video frame.</summary>
public sealed record ScanIntrinsics(double? Fx, double? Fy, double? Cx, double? Cy);

/// <summary>
/// One LiDAR depth sample: 16-bit PNG in millimetres (0 = no value) and its 8-bit confidence PNG (0/1/2), taken at
/// <see cref="T"/> (the frame with the same timestamp carries its pose).
/// </summary>
public sealed record ScanDepthFrame(double? T, int? Segment, string? File, string? ConfidenceFile, int? Width, int? Height);

/// <summary>The ARKit scene mesh: binary PLY, metres, ARKit world, with classification.</summary>
public sealed record ScanMesh(string? File);

/// <summary>A file of the package: its path in the package, size and SHA-256 (lowercase hex).</summary>
public sealed record ScanFileEntry(string? Name, long? Bytes, string? Sha256);
