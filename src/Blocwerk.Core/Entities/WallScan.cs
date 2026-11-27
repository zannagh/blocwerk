// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The package side of an iPhone scan: one row per <see cref="WallCapture"/> in mode
/// <see cref="WallCaptureGeometryMode.Device"/>, sharing its id (the manifest's <c>scanId</c>). The files live in the
/// capture store's <c>scans/{id}/</c> folder under their package names; the capture's status says whether the upload is
/// still open (<see cref="WallCaptureStatus.Draft"/>) or complete (<see cref="WallCaptureStatus.Received"/>).
/// </summary>
public class WallScan
{
    /// <summary>The capture's id, which is the manifest's <c>scanId</c>.</summary>
    [Key]
    public Guid CaptureId { get; set; }

    [ForeignKey(nameof(CaptureId))]
    public WallCapture Capture { get; set; } = null!;

    /// <summary>The manifest's <c>schemaVersion</c>, e.g. <c>blocwerk-scan/1</c>.</summary>
    [MaxLength(32)]
    public required string SchemaVersion { get; set; }

    /// <summary>The package manifest as uploaded (validated).</summary>
    public required string ManifestJson { get; set; }

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    [MaxLength(64)]
    public string? DeviceModel { get; set; }

    [MaxLength(64)]
    public string? AppVersion { get; set; }

    /// <summary>A generated test package (the manifest's <c>synthetic</c>), not a real recording; the UI marks it.</summary>
    public bool Synthetic { get; set; }

    /// <summary>When the commit found every file present and verified; null while the upload is open.</summary>
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>
    /// Reserved for processing: the scan's cleaned, coloured point cloud (binary PLY with normals, wall frame, metres), a
    /// path inside the scan's folder. Kept for the scan's lifetime; no retention rule removes it. Null until produced.
    /// </summary>
    [MaxLength(256)]
    public string? PointCloudPath { get; set; }

    public long? PointCloudBytes { get; set; }
}
