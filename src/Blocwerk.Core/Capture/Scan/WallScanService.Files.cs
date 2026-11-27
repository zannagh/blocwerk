// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>Put: each file streamed under its own lock, size- and hash-checked against the manifest, then moved into place.</summary>
public sealed partial class WallScanService
{
    public async Task<ScanFileState> PutFileAsync(
        Guid wallId, Guid scanId, string name, string? sha256, long? length, Stream body, CancellationToken ct)
    {
        var db = await OpenForUploaderAsync(wallId, ct);
        await using (db)
        {
            return await FileLocks.RunAsync(
                (scanId, name), () => PutLockedAsync(db, wallId, scanId, name, sha256, length, body, ct), ct);
        }
    }

    private async Task<ScanFileState> PutLockedAsync(
        BlocwerkDbContext db, Guid wallId, Guid scanId, string name, string? sha256, long? length, Stream body, CancellationToken ct)
    {
        var capture = await db.WallCaptures.AsNoTracking()
                          .FirstOrDefaultAsync(c => c.Id == scanId && c.WallId == wallId && c.GeometryMode == WallCaptureGeometryMode.Device, ct)
                      ?? throw new ScanUploadException(ScanUploadException.NotFound, "There is no scan with this id on this wall.");
        await EnsureCreatorAsync(db, capture, ct);
        var (version, expected) = await FileOfAsync(db, scanId, name, ct);
        if (sha256 is not null && !string.Equals(sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScanUploadException(ScanUploadException.Unprocessable, $"{name}: the SHA-256 sent is not the manifest's.");
        }

        if (!storage.Has(scanId, name, expected.Bytes!.Value))
        {
            EnsureOpen(capture);
            EnsureSpace(expected.Bytes.Value);
            try
            {
                await ReceiveAsync(scanId, expected, length, body, ct);
                await CaptureImportLocks.RunAsync(scanId, () => PlaceAsync(db, scanId, version, expected, ct), ct);
            }
            finally
            {
                // Its folder may not exist yet (a length refused before anything was written).
                if (File.Exists(storage.PartPath(scanId, name)))
                {
                    File.Delete(storage.PartPath(scanId, name));
                }
            }
        }

        return new ScanFileState(scanId, name, expected.Bytes.Value);
    }

    /// <summary>
    /// Moves the verified <c>.part</c> into place under the scan's lock, once it is sure the commit did not close the scan
    /// and a begin did not change the file's entry while it streamed.
    /// </summary>
    private async Task<bool> PlaceAsync(BlocwerkDbContext db, Guid scanId, long version, ScanFileEntry expected, CancellationToken ct)
    {
        var status = await db.WallCaptures.Where(c => c.Id == scanId).Select(c => (WallCaptureStatus?)c.Status).FirstOrDefaultAsync(ct);
        if (status != WallCaptureStatus.Draft)
        {
            throw new ScanUploadException(ScanUploadException.Conflict, "This scan is no longer taking files.");
        }

        if (ScanFileTables.Version(scanId) != version && (await FileOfAsync(db, scanId, expected.Name!, ct)).Entry != expected)
        {
            throw new ScanUploadException(
                ScanUploadException.Conflict, $"The scan was begun again with a changed {expected.Name} while it was sent; send it again.");
        }

        File.Move(storage.PartPath(scanId, expected.Name!), storage.FilePath(scanId, expected.Name!), overwrite: true);
        return true;
    }

    /// <summary>The manifest's entry for <paramref name="name"/> (from memory once read) and the version it belongs to.</summary>
    private static async Task<(long Version, ScanFileEntry Entry)> FileOfAsync(BlocwerkDbContext db, Guid scanId, string name, CancellationToken ct)
    {
        var table = await ScanFileTables.GetAsync(
            scanId, () => db.WallScans.AsNoTracking().Where(s => s.CaptureId == scanId).Select(s => s.ManifestJson).FirstOrDefaultAsync(ct));
        return table is { } t && t.Files.TryGetValue(name, out var entry)
            ? (t.Version, entry)
            : throw new ScanUploadException(ScanUploadException.NotFound, $"{name} is not a file of this scan.");
    }

    private static void EnsureOpen(WallCapture capture)
    {
        if (capture.Status != WallCaptureStatus.Draft)
        {
            throw new ScanUploadException(ScanUploadException.Conflict, "This scan is no longer taking files.");
        }
    }

    /// <summary>Streams the body into the file's <c>.part</c> and checks its size and SHA-256 against the manifest.</summary>
    private async Task ReceiveAsync(Guid scanId, ScanFileEntry expected, long? length, Stream body, CancellationToken ct)
    {
        var bytes = expected.Bytes!.Value;
        if (length > bytes)
        {
            throw new ScanUploadException(ScanUploadException.TooLarge, $"{expected.Name} is {length} bytes; the manifest says {bytes}.");
        }

        var part = storage.PartPath(scanId, expected.Name!);
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
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
        }
        catch (CaptureFileTooLargeException)
        {
            throw new ScanUploadException(ScanUploadException.TooLarge, $"{expected.Name} is larger than the manifest's {bytes} bytes.");
        }
    }
}
