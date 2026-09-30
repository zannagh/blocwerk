// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The export: a finished capture whose trained photo-real view is still kept on the server (its newest GPU job, installed,
/// with the runner's result and the prepared state; after a re-solve, the view its current model shares) and whose model
/// stands on its own (no correction of an earlier model, no facets carried over from one). Optional files that are gone (video, frames, sparse points) are left out with a
/// warning; a missing photo, texture or GPU file refuses the export.
/// </summary>
public sealed partial class CapturePackageService
{
    public async Task<CapturePackageManifest> ExportAsync(Guid captureId, CancellationToken ct)
    {
        var userId = await EnsureAdminAsync(ct);
        await using var db = dbContextFactory.CreateDbContext();
        var warnings = new List<string>();
        var rows = await LoadRowsAsync(db, captureId, warnings, ct);
        DropMissingOptionalFiles(rows.Capture, warnings);
        await ScoreUnscoredPhotosAsync(rows.Photos, ct);
        var packageFiles = await DescribeFilesAsync(rows, ct);
        var capture = rows.Capture;
        var planJson = capture.PlanRevision is { } revision
            ? await db.WallMarkerPlans.AsNoTracking()
                .Where(p => p.WallId == capture.WallId && p.Revision == revision).Select(p => p.Json).FirstOrDefaultAsync(ct)
            : null;
        logger.LogInformation(
            "Capture {CaptureId} exported as a package by {UserId}: {Files} files, {Bytes} bytes",
            captureId, userId, packageFiles.Count, packageFiles.Sum(f => f.Bytes));
        return new CapturePackageManifest
        {
            SourceAppVersion = typeof(CapturePackageService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            SourceMigration = NewestMigration(db),
            ExportedAt = DateTimeOffset.UtcNow,
            CaptureId = capture.Id,
            WallId = capture.WallId,
            OwnerUserId = capture.CreatedByUserId,
            PlanRevision = capture.PlanRevision,
            PlanJson = planJson,
            ReferenceModelId = RegisteredGeometry.Carried(rows.Model.Json).ReferenceModelId,
            Rows = rows,
            Files = packageFiles,
            Warnings = warnings,
        };
    }

    public async Task<string?> ExportFilePathAsync(Guid captureId, string name, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        if (!CapturePackageFiles.IsStoredName(name))
        {
            return null;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var rows = await LoadRowsAsync(db, captureId, [], ct);
        if (CapturePackageFiles.Referenced(rows).All(r => r.Name != name))
        {
            return null;
        }

        var path = files.ResolvePhysicalPath(name);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>The newest migration this build knows (null when the provider cannot tell).</summary>
    internal static string? NewestMigration(BlocwerkDbContext db)
    {
        try
        {
            return db.Database.GetMigrations().LastOrDefault();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<CapturePackageRows> LoadRowsAsync(BlocwerkDbContext db, Guid captureId, List<string> warnings, CancellationToken ct)
    {
        var capture = await db.WallCaptures.AsNoTracking().FirstOrDefaultAsync(c => c.Id == captureId, ct)
                      ?? throw new UserFacingException("There is no such capture.");
        if (capture.Status is not (WallCaptureStatus.Succeeded or WallCaptureStatus.SucceededWithoutTextures))
        {
            throw new UserFacingException($"The capture is {capture.Status}: only a capture finished with its photo-real view can be replayed.");
        }

        var model = capture.GeometryModelId is { } modelId
            ? await db.WallGeometryModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId, ct)
            : null;
        if (model is null)
        {
            throw new UserFacingException("The capture has no stored model.");
        }

        CheckStandalone(model);
        var job = await db.GpuJobs.AsNoTracking().Where(j => j.CaptureId == captureId).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (job is not { Status: GpuJobStatus.Succeeded, InstalledAt: not null, ResultPath: not null, RefinishStateJson: null })
        {
            throw new UserFacingException(
                "The capture has no trained photo-real view kept on this server (a 3D runner's installed result with its prepared state), so it cannot be replayed without training.");
        }

        await BindViewToModelAsync(db, job, model, warnings, ct);
        if (!model.IsActive)
        {
            warnings.Add("The capture's model is not the wall's active model here, so its photos fall under the photo retention (deleted 30 days after the capture ended by default): replay it soon.");
        }

        var photos = await db.WallCapturePhotos.AsNoTracking().Where(p => p.CaptureId == captureId).OrderBy(p => p.Index).ToListAsync(ct);
        var textures = await db.WallGeometryTextures.AsNoTracking().Where(t => t.GeometryModelId == model.Id).OrderBy(t => t.FacetId).ToListAsync(ct);
        return new CapturePackageRows(capture, photos, model, textures, job);
    }

    private static void CheckStandalone(WallGeometryModel model)
    {
        if (model.DerivedFromModelId is not null)
        {
            throw new UserFacingException("The capture's model is a correction of an earlier model, which the package does not carry.");
        }

        if (RegisteredGeometry.Carried(model.Json).CarriedFacets.Count > 0)
        {
            throw new UserFacingException("The capture's model carries facets over from the model it was registered to, which the package does not carry.");
        }
    }

    /// <summary>Leaves out the video, frames and sparse points that are gone (they are optional; the rows are changed).</summary>
    private void DropMissingOptionalFiles(WallCapture capture, List<string> warnings)
    {
        if (capture.VideoStoredPath is { } video && !Stored(video))
        {
            capture.VideoStoredPath = null;
            warnings.Add("The walk-along video is gone on the source; the package leaves it out.");
        }

        var frames = CaptureVideoFiles.Frames(capture.VideoFramesJson);
        var kept = frames.Where(Stored).ToList();
        if (kept.Count < frames.Count)
        {
            capture.VideoFramesJson = kept.Count == 0 ? null : CaptureVideoFiles.FramesJson(kept);
            warnings.Add($"{frames.Count - kept.Count} video frame(s) are gone on the source; the package leaves them out.");
        }

        if (capture.SparsePointsStoredPath is { } sparse && !Stored(sparse))
        {
            capture.SparsePointsStoredPath = null;
            warnings.Add("The sparse points are gone on the source; the package leaves them out.");
        }
    }

    private async Task<List<CapturePackageFile>> DescribeFilesAsync(CapturePackageRows rows, CancellationToken ct)
    {
        var referenced = CapturePackageFiles.Referenced(rows);
        var missing = referenced.Where(r => !Stored(r.Name)).ToList();
        if (missing.Count > 0)
        {
            throw new UserFacingException(
                $"{missing.Count} file(s) of the capture are missing on this server ({string.Join(", ", missing.Take(5).Select(m => $"{m.Role} {m.Name}"))}), so it cannot be replayed.");
        }

        var described = new List<CapturePackageFile>();
        foreach (var (name, role) in referenced)
        {
            var path = files.ResolvePhysicalPath(name)!;
            described.Add(new CapturePackageFile(name, new FileInfo(path).Length, await CapturePackageFiles.Sha256Async(path, ct), role));
        }

        return described;
    }

    private bool Stored(string name) => files.ResolvePhysicalPath(name) is { } path && File.Exists(path);
}
