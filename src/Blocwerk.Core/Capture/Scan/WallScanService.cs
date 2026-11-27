// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// The scan upload (<see cref="IWallScanService"/>). A scan is a <see cref="WallCapture"/> in mode
/// <see cref="WallCaptureGeometryMode.Device"/> with the manifest's <c>scanId</c> as its id, plus its
/// <see cref="WallScan"/> row. Begin and commit run under the scan id's <see cref="CaptureImportLocks"/> lock; a PUT
/// streams under a lock of its own file only (so the files of one scan arrive in parallel) and takes the scan's lock just
/// to move the verified file into place, so it never races a begin that changed the manifest or the commit. Only the
/// user who began a scan, or an administrator of the installation, may continue it. The files go to
/// <see cref="ScanStorage"/>.
/// </summary>
public sealed partial class WallScanService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    ICaptureFileStore files,
    ILogger<WallScanService> logger,
    IKioskContext? kioskContext = null) : IWallScanService
{
    /// <summary>What the wall's capture page shows for a scan whose package is complete.</summary>
    public const string ReceivedStage = "Received, waiting for processing";

    private const string UploadingStage = "Receiving the iPhone scan";
    private const string Action = "Uploading an iPhone scan";

    /// <summary>Room kept free on the store's volume beyond what the package still needs.</summary>
    private const long FreeSpaceMargin = 2L * 1024 * 1024 * 1024;

    /// <summary>Open (uncommitted) scans one user may have at a time, over all walls.</summary>
    public const int MaxOpenScansPerUser = 2;

    /// <summary>Open (uncommitted) scans one wall may have at a time.</summary>
    public const int MaxOpenScansPerWall = 4;

    /// <summary>One lock per file of a scan while it streams.</summary>
    private static readonly KeyedAsyncLocks<(Guid ScanId, string Name)> FileLocks = new();

    /// <summary>Counting the open scans and opening a new one run one at a time, so two begins never both slip under a cap.</summary>
    private static readonly SemaphoreSlim OpenGate = new(1, 1);

    private readonly ScanStorage storage = new(files);

    /// <summary>Free bytes on the store's volume; tests replace it to see a full disk.</summary>
    internal Func<long> FreeSpace { get; set; } = () => new ScanStorage(files).FreeBytes();

    /// <summary>True while a request works on the scan: begin, commit, or a file streaming in.</summary>
    /// <param name="scanId">The scan.</param>
    /// <returns>Whether retention must leave the scan alone for now.</returns>
    internal static bool IsBusy(Guid scanId) => CaptureImportLocks.Has(scanId) || FilesBusy(scanId);

    /// <summary>True while a file of the scan streams in (its PUT holds no scan lock until it moves the file).</summary>
    /// <param name="scanId">The scan.</param>
    /// <returns>Whether any file lock of the scan is held or waited for.</returns>
    internal static bool FilesBusy(Guid scanId) => FileLocks.Any(k => k.ScanId == scanId);

    public async Task<ScanUploadState> GetAsync(Guid wallId, Guid scanId, CancellationToken ct)
    {
        var db = await OpenForUploaderAsync(wallId, ct);
        await using (db)
        {
            var (capture, _, manifest) = await LoadAsync(db, wallId, scanId, ct);
            return State(capture, manifest);
        }
    }

    /// <summary>
    /// Opens a context for a caller who may upload scans to <paramref name="wallId"/>: the wall's owner, an admin or a
    /// moderator of it, or an administrator of the installation; never a kiosk (or visitor) session. A wall key, kiosk key
    /// or share link never reaches here (the route's policy admits browser users and personal write keys only).
    /// </summary>
    private async Task<BlocwerkDbContext> OpenForUploaderAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, Action);
            WallAdminGuard.EnsureNotForeignWall(db, wallId);
            if (KioskViewing.IsVisitor(kioskContext)
                || !(await WallAdminGuard.IsWallModeratorOrAboveAsync(db, wallId, user.Id, ct) || await AppAdminGuard.IsAppAdminAsync(db, user.Id, ct)))
            {
                throw new UnauthorizedAccessException($"User {user.Id} may not upload scans to wall {wallId}.");
            }

            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>Refuses a caller who neither began the scan nor administers the installation.</summary>
    private static async Task EnsureCreatorAsync(BlocwerkDbContext db, WallCapture capture, CancellationToken ct)
    {
        if (capture.CreatedByUserId != db.CurrentUserId && !await AppAdminGuard.IsAppAdminAsync(db, db.CurrentUserId, ct))
        {
            throw new ScanUploadException(
                ScanUploadException.Forbidden,
                "Someone else began this scan; only they or an administrator of the installation may continue it.");
        }
    }

    /// <summary>The scan's capture, row and manifest; 404 when the id is no scan of this wall.</summary>
    private static async Task<(WallCapture Capture, WallScan Scan, ScanManifest Manifest)> LoadAsync(
        BlocwerkDbContext db, Guid wallId, Guid scanId, CancellationToken ct)
    {
        var capture = await db.WallCaptures.FirstOrDefaultAsync(
                          c => c.Id == scanId && c.WallId == wallId && c.GeometryMode == WallCaptureGeometryMode.Device, ct)
                      ?? throw new ScanUploadException(ScanUploadException.NotFound, "There is no scan with this id on this wall.");
        var scan = await db.WallScans.FirstAsync(s => s.CaptureId == scanId, ct);
        var manifest = JsonSerializer.Deserialize<ScanManifest>(scan.ManifestJson, CapturePackageFiles.Json)!;
        return (capture, scan, manifest);
    }

    private ScanUploadState State(WallCapture capture, ScanManifest manifest)
    {
        var listed = manifest.Files ?? [];
        var missing = listed.Where(f => !storage.HasFolder || !storage.Has(capture.Id, f.Name!, f.Bytes!.Value)).ToList();
        var total = listed.Sum(f => f.Bytes ?? 0);
        return new ScanUploadState(
            capture.Id,
            capture.WallId,
            capture.Status == WallCaptureStatus.Received ? ScanUploadState.Received : ScanUploadState.Uploading,
            capture.Stage,
            missing.Select(f => f.Name!).ToList(),
            listed.Count,
            total,
            total - missing.Sum(f => f.Bytes ?? 0));
    }
}
