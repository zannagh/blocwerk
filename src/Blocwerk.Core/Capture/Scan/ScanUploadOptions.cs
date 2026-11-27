// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// How much the scan upload keeps on disk. A received scan is never removed by retention, so these bound what waits:
/// scans per wall that are not applied yet, and bytes one user may commit in a rolling window over all walls. A site
/// admin removes a waiting scan by hand (<see cref="IWallScanService.DeleteAsync"/>).
/// </summary>
public sealed class ScanUploadOptions
{
    /// <summary>
    /// Received scans one wall may hold that are not applied yet. Setting <c>Blocwerk:Scan:MaxWaitingPerWall</c> /
    /// <c>SCAN__MAXWAITINGPERWALL</c> (1–100); default 3.
    /// </summary>
    public int MaxWaitingScansPerWall { get; init; } = 3;

    /// <summary>
    /// Committed scan bytes one user may upload in <see cref="UserWindow"/>, over all walls. Setting
    /// <c>Blocwerk:Scan:MaxGbPerUser</c> / <c>SCAN__MAXGBPERUSER</c> (1–10000); default 20 GB.
    /// </summary>
    public long MaxBytesPerUser { get; init; } = 20L * 1024 * 1024 * 1024;

    /// <summary>
    /// The rolling window of <see cref="MaxBytesPerUser"/>. Setting <c>Blocwerk:Scan:UserWindowDays</c> /
    /// <c>SCAN__USERWINDOWDAYS</c> (1–365); default 30 days.
    /// </summary>
    public TimeSpan UserWindow { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The defaults, with each limit read from configuration when set.</summary>
    public static ScanUploadOptions Bind(IConfiguration? configuration)
    {
        var defaults = new ScanUploadOptions();
        return new ScanUploadOptions
        {
            MaxWaitingScansPerWall = ReadInt(configuration, "MaxWaitingPerWall", 1, 100) ?? defaults.MaxWaitingScansPerWall,
            MaxBytesPerUser = ReadInt(configuration, "MaxGbPerUser", 1, 10_000) is { } gb ? gb * 1024L * 1024 * 1024 : defaults.MaxBytesPerUser,
            UserWindow = ReadInt(configuration, "UserWindowDays", 1, 365) is { } days ? TimeSpan.FromDays(days) : defaults.UserWindow,
        };
    }

    private static int? ReadInt(IConfiguration? configuration, string key, int min, int max) =>
        int.TryParse(
            configuration?[$"Blocwerk:Scan:{key}"] ?? Environment.GetEnvironmentVariable($"SCAN__{key.ToUpperInvariant()}"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
        && value >= min && value <= max
            ? value
            : null;
}
