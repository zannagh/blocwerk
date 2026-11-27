// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>One scan of a wall, as the wall's settings list it.</summary>
/// <param name="ScanId">The scan.</param>
/// <param name="CreatedAt">When the upload began.</param>
/// <param name="ReceivedAt">When it was complete; null while uploading.</param>
/// <param name="Stage">What is happening with it ("Received, waiting for processing").</param>
/// <param name="DeviceModel">The phone.</param>
/// <param name="TotalBytes">The package size.</param>
public sealed record WallScanSummary(
    Guid ScanId, DateTimeOffset CreatedAt, DateTimeOffset? ReceivedAt, string? Stage, string? DeviceModel, long TotalBytes);
