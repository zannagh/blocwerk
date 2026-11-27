// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// Checks a <c>blocwerk-scan/1</c> manifest before a byte of the package is taken: schema version, identity, the HEVC
/// format and size, the exposure lock (whole recording or per segment), the optional wall box reach and coverage, the
/// file list (names, sizes, hashes, totals), segments, every frame's timestamp, pose
/// and intrinsics, the depth samples and the mesh, and that every listed file is used and every used file listed.
/// Returns plain sentences naming the field (empty = valid).
/// </summary>
public static partial class ScanManifestValidator
{
    private const double Eps = 1e-6;

    /// <summary>Validates <paramref name="m"/> for an upload to <paramref name="wallId"/>.</summary>
    public static IReadOnlyList<string> Validate(ScanManifest m, Guid wallId)
    {
        var errors = new ScanManifestErrors();
        if (m.SchemaVersion != ScanPackageLimits.SchemaV1)
        {
            errors.Add($"Unknown schemaVersion \"{m.SchemaVersion}\": this server reads \"{ScanPackageLimits.SchemaV1}\".");
            return errors.ToList();
        }

        CheckHeader(m, wallId, errors);
        var size = CheckVideoFormat(m.Video, errors);
        CheckExposureLocks(m, errors);
        CheckWallBox(m.WallBox, errors);
        CheckWallBoxReach(m.WallBoxReach, errors);
        CheckCoverage(m.Coverage, errors);
        var files = CheckFiles(m.Files, errors);
        var segments = CheckSegments(m.Segments, files, errors);
        CheckFrames(m.Frames, segments, size, errors);
        CheckDepth(m.Depth, m.Frames?.Count ?? 0, segments, files, errors);
        CheckMesh(m.Mesh, files, errors);
        CheckUnreferenced(m, files, errors);
        return errors.ToList();
    }

    private static void CheckHeader(ScanManifest m, Guid wallId, ScanManifestErrors errors)
    {
        if (m.ScanId is null || m.ScanId == Guid.Empty)
        {
            errors.Add("scanId is missing.");
        }

        if (m.WallId is { } sent && sent != wallId)
        {
            errors.Add($"wallId {sent} is not the wall this upload goes to ({wallId}).");
        }

        if (m.StartedAt is null)
        {
            errors.Add("startedAt is missing.");
        }

        if (string.IsNullOrWhiteSpace(m.App?.Version) || m.App.Version.Length > 64)
        {
            errors.Add("app.version is missing (or longer than 64 characters).");
        }

        if (string.IsNullOrWhiteSpace(m.Device?.Model) || m.Device.Model.Length > 64 || string.IsNullOrWhiteSpace(m.Device.OsVersion))
        {
            errors.Add("device.model and device.osVersion are required (model at most 64 characters).");
        }
    }

    private static void CheckWallBox(IReadOnlyList<double[]>? box, ScanManifestErrors errors)
    {
        if (box is not { Count: 3 } || box.Any(p => p is not { Length: 3 } || p.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100)))
        {
            errors.Add("wallBox must be three points of three finite coordinates in metres (within 100 m).");
        }
    }
}
