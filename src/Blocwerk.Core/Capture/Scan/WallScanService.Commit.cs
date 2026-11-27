// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>Commit: once every file is in place and verified, the scan is marked received.</summary>
public sealed partial class WallScanService
{
    public async Task<ScanUploadState> CommitAsync(Guid wallId, Guid scanId, CancellationToken ct)
    {
        var db = await OpenForUploaderAsync(wallId, ct);
        await using (db)
        {
            return await CaptureImportLocks.RunAsync(
                scanId,
                async () =>
                {
                    var (capture, scan, manifest) = await LoadAsync(db, wallId, scanId, ct);
                    await EnsureCreatorAsync(db, capture, ct);
                    var state = State(capture, manifest);
                    if (capture.Status == WallCaptureStatus.Received)
                    {
                        return state;
                    }

                    if (state.Missing.Count > 0)
                    {
                        throw new ScanUploadException(
                            ScanUploadException.Conflict, $"{state.Missing.Count} file(s) of this scan are still missing.", state.Missing);
                    }

                    EnsureOpen(capture);
                    capture.Status = WallCaptureStatus.Received;
                    capture.Stage = ReceivedStage;
                    scan.ReceivedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    ScanFileTables.Forget(scanId);
                    logger.LogInformation("Scan {ScanId} on wall {WallId}: received ({Bytes} bytes)", scanId, wallId, state.TotalBytes);
                    return State(capture, manifest);
                },
                ct);
        }
    }
}
