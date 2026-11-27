// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>The companion app that wrote the package.</summary>
public sealed record ScanApp(string? Version, string? Build);

/// <summary>The phone: model identifier (e.g. <c>iPhone17,1</c>) and iOS version.</summary>
public sealed record ScanDevice(string? Model, string? OsVersion);

/// <summary>
/// The recording's video format: HEVC from 1280×720 up to 3840×2160, 16:9 or 4:3; frame intrinsics are in pixels of
/// this size.
/// </summary>
public sealed record ScanVideoFormat(string? Codec, int? Width, int? Height, double? Fps);

/// <summary>
/// The camera settings locked when recording started (<c>configurableCaptureDeviceForPrimaryCamera</c>), so every frame
/// shares exposure, white balance and focus; per segment, the flags say which of the three the device really locked.
/// </summary>
public sealed record ScanExposureLock(
    double? ExposureDurationSeconds,
    double? Iso,
    double? WhiteBalanceRed,
    double? WhiteBalanceGreen,
    double? WhiteBalanceBlue,
    double? LensPosition,
    double? LockedAt,
    bool? ExposureLocked = null,
    bool? WhiteBalanceLocked = null,
    bool? FocusLocked = null);

/// <summary>
/// A tracking segment (its own ARKit world frame after a failed relocalization), its video pieces and, optionally, the
/// exposure lock it was recorded with.
/// </summary>
public sealed record ScanSegment(
    int? Id, double? StartTime, double? EndTime, IReadOnlyList<ScanVideoPiece>? Videos, ScanExposureLock? ExposureLock = null);

/// <summary>One HEVC piece of a segment's recording (about 60 s), starting at <see cref="StartTime"/>.</summary>
public sealed record ScanVideoPiece(string? File, double? StartTime);

/// <summary>
/// One video frame: timestamp, segment, camera-to-world <see cref="Pose"/> (16 numbers, column-major, ARKit camera
/// x right, y up, looking down −Z), intrinsics at the video resolution, the tracking state and, optionally, the camera
/// settings of this frame, to verify the lock held.
/// </summary>
public sealed record ScanFrame(
    double? T, int? Segment, double[]? Pose, ScanIntrinsics? Intrinsics, string? Tracking, ScanFrameCamera? Camera = null);

/// <summary>One frame's camera settings: lens position, ISO, exposure and the three white balance gains (red, green, blue).</summary>
public sealed record ScanFrameCamera(double? LensPosition, double? Iso, double? ExposureDurationSeconds, double[]? WhiteBalanceGains);

/// <summary>How far the wall reaches in front of (<see cref="FrontMeters"/>) and behind its box's plane, in metres.</summary>
public sealed record ScanWallBoxReach(double? FrontMeters, double? BackMeters);

/// <summary>
/// The app's coverage summary: the share of the wall's patches it saw well enough (0–100), the patch size, and how
/// many patches each viewing direction covered, by bin name (<c>frontal</c>, <c>fromLeft</c>, <c>fromRight</c>,
/// <c>fromAbove</c>, <c>fromBelow</c>, <c>lookingUp</c>; other names are kept).
/// </summary>
public sealed record ScanCoverage(double? PercentGreen, double? PatchSizeMeters, IReadOnlyDictionary<string, int>? Bins);

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
