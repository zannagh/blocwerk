// <copyright file="CaptureDetectionRecord.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The hold detections of one capture photo by one detector, kept so a hold search that a restart or a deploy cut off
/// resumes with the photos it has not looked at yet instead of starting over (minutes per hundred photos on the CPU).
/// Derived data: never journalled, pruned after <c>CaptureDetectionStore.Retention</c>.
/// </summary>
public class CaptureDetectionRecord
{
    /// <summary>The photo's stored name in the capture file store.</summary>
    [MaxLength(512)]
    public string StoredPath { get; set; } = string.Empty;

    /// <summary>The detector that found them.</summary>
    [MaxLength(128)]
    public string Detector { get; set; } = string.Empty;

    /// <summary>The detections on the solved camera's grid, as JSON.</summary>
    public string DetectionsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
