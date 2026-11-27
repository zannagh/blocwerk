// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Linq.Expressions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// What received scans may keep on disk (<see cref="ScanUploadOptions"/>): retention never removes a received scan, so a
/// wall holds at most a few that wait, and one user commits at most so many bytes in a rolling window. Checked when a new
/// scan begins and again when one commits, so scans begun in parallel cannot all slip under a limit.
/// </summary>
public sealed partial class WallScanService
{
    /// <summary>
    /// A scan that takes room under the limits: received, not applied yet. Once processing applies a scan it moves on to a
    /// status of its own and leaves this set; nothing applies one yet.
    /// </summary>
    internal static readonly Expression<Func<WallCapture, bool>> Waiting =
        c => c.GeometryMode == WallCaptureGeometryMode.Device && c.Status == WallCaptureStatus.Received;

    /// <summary>409 when the wall holds its share of waiting scans, or the user's window would go over with <paramref name="bytes"/>.</summary>
    private async Task EnsureUnderQuotaAsync(BlocwerkDbContext db, Guid wallId, Guid userId, long bytes, CancellationToken ct)
    {
        var onWall = await db.WallCaptures.AsNoTracking().Where(Waiting).CountAsync(c => c.WallId == wallId, ct);
        if (onWall >= scanOptions.MaxWaitingScansPerWall)
        {
            throw new ScanUploadException(
                ScanUploadException.Conflict,
                $"This wall already has {scanOptions.MaxWaitingScansPerWall} scans waiting; a site admin can remove one.");
        }

        var used = await BytesInWindowAsync(db, userId, ct);
        if (used + bytes > scanOptions.MaxBytesPerUser)
        {
            throw new ScanUploadException(
                ScanUploadException.Conflict,
                $"You have uploaded {Gb(used)} of scans in the last {scanOptions.UserWindow.TotalDays:0} days and this one is {Gb(bytes)}; "
                + $"the limit is {Gb(scanOptions.MaxBytesPerUser)}. A site admin can remove one of your waiting scans.");
        }
    }

    /// <summary>The bytes of the user's waiting scans received inside the window, over all walls.</summary>
    private async Task<long> BytesInWindowAsync(BlocwerkDbContext db, Guid userId, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - scanOptions.UserWindow;
        var mine = db.WallCaptures.Where(Waiting).Where(c => c.CreatedByUserId == userId).Select(c => c.Id);
        var received = await db.WallScans.AsNoTracking()
            .Where(s => mine.Contains(s.CaptureId))
            .Select(s => new { s.ReceivedAt, s.TotalBytes })
            .ToListAsync(ct);

        // Filtered in memory: SQLite (the tests) cannot compare DateTimeOffset.
        return received.Where(s => s.ReceivedAt >= since).Sum(s => s.TotalBytes);
    }

    private static string Gb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
}
