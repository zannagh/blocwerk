// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>The wall's recent scans, for its settings page.</summary>
public sealed partial class WallScanService
{
    public async Task<IReadOnlyList<WallScanSummary>> ListAsync(Guid wallId, CancellationToken ct)
    {
        var db = await OpenForUploaderAsync(wallId, ct);
        await using (db)
        {
            var rows = await db.WallScans.AsNoTracking()
                .Where(s => s.Capture.WallId == wallId && s.Capture.GeometryMode == WallCaptureGeometryMode.Device)
                .Select(s => new { s.CaptureId, s.Capture.CreatedAt, s.ReceivedAt, s.Capture.Stage, s.DeviceModel, s.TotalBytes, s.Synthetic })
                .ToListAsync(ct);

            // Ordered in memory: SQLite (the tests) cannot order by DateTimeOffset.
            return rows.OrderByDescending(r => r.CreatedAt)
                .Take(10)
                .Select(r => new WallScanSummary(r.CaptureId, r.CreatedAt, r.ReceivedAt, r.Stage, r.DeviceModel, r.TotalBytes, r.Synthetic))
                .ToList();
        }
    }
}
