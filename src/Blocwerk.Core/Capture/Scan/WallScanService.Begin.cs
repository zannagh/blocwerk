// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>Begin: the manifest is validated, the scan opened (or resumed), and the missing files are named.</summary>
public sealed partial class WallScanService
{
    public async Task<ScanUploadState> BeginAsync(Guid wallId, ScanManifest manifest, CancellationToken ct)
    {
        var db = await OpenForUploaderAsync(wallId, ct);
        await using (db)
        {
            var mode = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => (WallUpdateMode?)w.UpdateMode).FirstOrDefaultAsync(ct)
                       ?? throw new ScanUploadException(ScanUploadException.NotFound, "There is no such wall.");
            if (mode != WallUpdateMode.Scan)
            {
                throw new ScanUploadException(ScanUploadException.Conflict, "Switch on \"iPhone scan\" in this wall's settings first.");
            }

            var problems = ScanManifestValidator.Validate(manifest, wallId);
            if (problems.Count > 0)
            {
                throw new ScanUploadException(ScanUploadException.BadRequest, "The scan manifest is not valid.", problems);
            }

            var scanId = manifest.ScanId!.Value;
            return await CaptureImportLocks.RunAsync(scanId, () => BeginLockedAsync(db, wallId, scanId, manifest, ct), ct);
        }
    }

    private async Task<ScanUploadState> BeginLockedAsync(BlocwerkDbContext db, Guid wallId, Guid scanId, ScanManifest manifest, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(manifest, CapturePackageFiles.Json);
        var capture = await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == scanId, ct);
        if (capture is null)
        {
            capture = Open(db, wallId, scanId, manifest, json);
        }
        else if (capture.WallId != wallId || capture.GeometryMode != WallCaptureGeometryMode.Device)
        {
            throw new ScanUploadException(ScanUploadException.Conflict, "This scanId is already in use; start the scan again with a new one.");
        }
        else
        {
            var scan = await db.WallScans.FirstAsync(s => s.CaptureId == scanId, ct);
            if (capture.Status != WallCaptureStatus.Draft)
            {
                return scan.ManifestJson == json
                    ? State(capture, manifest)
                    : throw new ScanUploadException(ScanUploadException.Conflict, "This scan was received already; a changed package needs a new scanId.");
            }

            DropChangedFiles(scanId, JsonSerializer.Deserialize<ScanManifest>(scan.ManifestJson, CapturePackageFiles.Json)!, manifest);
            Describe(scan, manifest, json);
        }

        var state = State(capture, manifest);
        EnsureSpace(state);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Scan {ScanId} on wall {WallId}: begun, {Missing} of {Total} file(s) missing", scanId, wallId, state.Missing.Count, state.TotalFiles);
        return state;
    }

    private WallCapture Open(BlocwerkDbContext db, Guid wallId, Guid scanId, ScanManifest manifest, string json)
    {
        if (!storage.HasFolder)
        {
            throw new InvalidOperationException("The capture store has no folder for scans.");
        }

        var capture = new WallCapture
        {
            Id = scanId,
            WallId = wallId,
            CreatedByUserId = db.CurrentUserId,
            GeometryMode = WallCaptureGeometryMode.Device,
            Stage = UploadingStage,
            Notes = $"iPhone scan ({manifest.Device?.Model})",
        };
        var scan = new WallScan { CaptureId = scanId, SchemaVersion = ScanPackageLimits.SchemaV1, ManifestJson = json };
        Describe(scan, manifest, json);
        db.WallCaptures.Add(capture);
        db.WallScans.Add(scan);
        Directory.CreateDirectory(storage.Folder(scanId));
        return capture;
    }

    private static void Describe(WallScan scan, ScanManifest manifest, string json)
    {
        scan.ManifestJson = json;
        scan.FileCount = manifest.Files!.Count;
        scan.TotalBytes = manifest.Files.Sum(f => f.Bytes ?? 0);
        scan.DeviceModel = manifest.Device?.Model;
        scan.AppVersion = manifest.App?.Version;
    }

    /// <summary>A received file the new manifest no longer lists with the same hash is dropped, so it is sent again.</summary>
    private void DropChangedFiles(Guid scanId, ScanManifest previous, ScanManifest now)
    {
        var current = (now.Files ?? []).ToDictionary(f => f.Name!, f => f.Sha256, StringComparer.Ordinal);
        foreach (var old in (previous.Files ?? []).Where(f => !current.TryGetValue(f.Name!, out var sha) || sha != f.Sha256))
        {
            File.Delete(storage.FilePath(scanId, old.Name!));
        }
    }

    private void EnsureSpace(ScanUploadState state)
    {
        var needed = state.TotalBytes - state.ReceivedBytes + FreeSpaceMargin;
        if (storage.FreeBytes() < needed)
        {
            throw new ScanUploadException(
                ScanUploadException.Conflict, $"The server has not enough free space for this scan ({needed / (1024 * 1024)} MB needed).");
        }
    }
}
