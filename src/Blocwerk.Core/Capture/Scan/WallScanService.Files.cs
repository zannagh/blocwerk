// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>Put and commit: each file streamed, size- and hash-checked against the manifest; the commit marks it received.</summary>
public sealed partial class WallScanService
{
    public async Task<ScanUploadState> PutFileAsync(
        Guid wallId, Guid scanId, string name, string? sha256, long? length, Stream body, CancellationToken ct)
    {
        var db = await OpenForUploaderAsync(wallId, ct);
        await using (db)
        {
            return await CaptureImportLocks.RunAsync(
                scanId,
                async () =>
                {
                    var (capture, _, manifest) = await LoadAsync(db, wallId, scanId, ct);
                    var expected = manifest.Files!.FirstOrDefault(f => f.Name == name)
                                   ?? throw new ScanUploadException(ScanUploadException.NotFound, $"{name} is not a file of this scan.");
                    if (sha256 is not null && !string.Equals(sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ScanUploadException(ScanUploadException.Unprocessable, $"{name}: the SHA-256 sent is not the manifest's.");
                    }

                    if (!storage.Has(scanId, name, expected.Bytes!.Value))
                    {
                        EnsureOpen(capture);
                        await ReceiveAsync(scanId, expected, length, body, ct);
                    }

                    return State(capture, manifest);
                },
                ct);
        }
    }

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
                    logger.LogInformation("Scan {ScanId} on wall {WallId}: received ({Bytes} bytes)", scanId, wallId, state.TotalBytes);
                    return State(capture, manifest);
                },
                ct);
        }
    }

    private static void EnsureOpen(WallCapture capture)
    {
        if (capture.Status != WallCaptureStatus.Draft)
        {
            throw new ScanUploadException(ScanUploadException.Conflict, "This scan is no longer taking files.");
        }
    }

    private async Task ReceiveAsync(Guid scanId, ScanFileEntry expected, long? length, Stream body, CancellationToken ct)
    {
        var bytes = expected.Bytes!.Value;
        if (length > bytes)
        {
            throw new ScanUploadException(ScanUploadException.TooLarge, $"{expected.Name} is {length} bytes; the manifest says {bytes}.");
        }

        var target = storage.FilePath(scanId, expected.Name!);
        var part = storage.PartPath(scanId, expected.Name!);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            (long Bytes, string Sha256) received;
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                received = await CapturePackageFiles.CopyHashedAsync(body, file, bytes, ct);
            }

            if (received.Bytes != bytes || received.Sha256 != expected.Sha256)
            {
                throw new ScanUploadException(
                    ScanUploadException.Unprocessable,
                    $"{expected.Name} arrived with {received.Bytes} bytes and SHA-256 {received.Sha256}, not {bytes} bytes and {expected.Sha256}.");
            }

            File.Move(part, target, overwrite: true);
        }
        catch (CaptureFileTooLargeException)
        {
            throw new ScanUploadException(ScanUploadException.TooLarge, $"{expected.Name} is larger than the manifest's {bytes} bytes.");
        }
        finally
        {
            File.Delete(part);
        }
    }
}
