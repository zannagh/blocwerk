// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Corrections;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>Opening a correction (the guards) and writing its result as the wall's new active model version.</summary>
public sealed partial class WallGeometryCorrectionService
{
    /// <summary>The source that tags a correction's model (<see cref="WallGeometryModel.Source"/>).</summary>
    public const string SourcePrefix = "correction ";

    /// <summary>The admin context, the active model and its document, and the capture that points at the model.</summary>
    private async Task<CorrectionContext> OpenCorrectionAsync(Guid wallId)
    {
        var (db, userId) = await OpenForAdminAsync(wallId);
        try
        {
            var model = await db.WallGeometryModels.AsNoTracking().FirstOrDefaultAsync(m => m.WallId == wallId && m.IsActive)
                        ?? throw new UserFacingException("This wall has no 3D model yet.");
            var document = Parse(model.Json) ?? throw new UserFacingException("The 3D model can no longer be read, so it cannot be corrected.");
            var running = await db.WallCaptures.AnyAsync(c => c.WallId == wallId
                && (c.Status == WallCaptureStatus.Queued || c.Status == WallCaptureStatus.Detecting
                    || c.Status == WallCaptureStatus.Solving || c.Status == WallCaptureStatus.Texturing
                    || c.Status == WallCaptureStatus.Splatting));
            if (running)
            {
                throw new UserFacingException("A capture of this wall is still being processed. Correct the model once it is done.");
            }

            var captureId = await db.WallCaptures.AsNoTracking()
                .Where(c => c.WallId == wallId && c.GeometryModelId == model.Id)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync();
            return new CorrectionContext(db, userId, model, document, captureId);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Stores <paramref name="json"/> as the wall's new ACTIVE model derived from the current one, with the current one's
    /// textures (bounds mapped, files shared) and photo-real view (frame mapped, files shared); points the capture at it
    /// and queues its follow-up chain. One transaction; the old model stays in the history.
    /// </summary>
    private async Task<GeometryCorrectionResult> SaveVersionAsync(
        CorrectionContext context, string json, GeometrySimilarity t, string? dropped, GeometryCorrectionKind kind, string summary)
    {
        var (document, errors) = WallGlyphService.ParseAndValidate(json);
        if (document is null)
        {
            logger.LogWarning("Correction {Kind} of model {ModelId} produced an invalid model: {Errors}", kind, context.Model.Id, string.Join("; ", errors));
            throw new UserFacingException("The corrected model would not be valid, so nothing was changed.");
        }

        var db = context.Db;
        var old = context.Model;
        var span = WallGeometrySummary.MarkerSpan(document);
        var model = new WallGeometryModel
        {
            WallId = old.WallId,
            Json = json,
            SchemaVersion = document.Version,
            Source = SourcePrefix + kind.ToString().ToLowerInvariant(),
            CreatedByUserId = context.UserId,
            IsActive = true,
            PlanRevision = old.PlanRevision,
            FrameSource = old.FrameSource,
            DerivedFromModelId = old.Id,
            WidthMm = span?.WidthMm,
            HeightMm = span?.HeightMm,
            ReprojRmsPx = old.ReprojRmsPx,
            Notes = summary.Length <= 2048 ? summary : summary[..2048],
        };
        var textures = await CopyTexturesAsync(db, old.Id, model.Id, t, dropped);
        var splat = await CopySplatAsync(db, old.Id, model.Id, t);
        await WallGlyphService.SwapActiveModelAsync(db, old.WallId, document, keep: null, () =>
        {
            db.WallGeometryModels.Add(model);
            db.WallGeometryTextures.AddRange(textures);
            if (splat is not null)
            {
                db.WallGeometrySplats.Add(splat);
            }
        });

        var captureId = await RepointCaptureAsync(db, context.CaptureId, model.Id);
        if (captureId is { } id)
        {
            followUps.Enqueue(id);
        }

        logger.LogInformation(
            "Wall {WallId}: model {ModelId} corrected ({Kind}) into {NewModelId} by {UserId}: {Summary}",
            old.WallId, old.Id, kind, model.Id, context.UserId, summary);
        return new GeometryCorrectionResult(kind, model.Id, old.Id, t.Scale, t.RotationDeg, summary);
    }

    private static async Task<List<WallGeometryTexture>> CopyTexturesAsync(
        BlocwerkDbContext db, Guid from, Guid to, GeometrySimilarity t, string? dropped)
    {
        var rows = await db.WallGeometryTextures.AsNoTracking().Where(x => x.GeometryModelId == from && x.FacetId != dropped).ToListAsync();
        return rows.Select(x =>
        {
            var b = WallGeometryModelTransformer.TransformTexture(new TextureBounds(x.AMin, x.AMax, x.BMin, x.BMax), t);
            return new WallGeometryTexture
            {
                GeometryModelId = to,
                FacetId = x.FacetId,
                StoredPath = x.StoredPath,
                ContentType = x.ContentType,
                SizeBytes = x.SizeBytes,
                MaskStoredPath = x.MaskStoredPath,
                MaskSizeBytes = x.MaskSizeBytes,
                SourceMapStoredPath = x.SourceMapStoredPath,
                SourceMapSizeBytes = x.SourceMapSizeBytes,
                AMin = b.AMin,
                AMax = b.AMax,
                BMin = b.BMin,
                BMax = b.BMax,
                WidthPx = x.WidthPx,
                HeightPx = x.HeightPx,
            };
        }).ToList();
    }

    private static async Task<WallGeometrySplat?> CopySplatAsync(BlocwerkDbContext db, Guid from, Guid to, GeometrySimilarity t)
    {
        var x = await db.WallGeometrySplats.AsNoTracking().FirstOrDefaultAsync(s => s.GeometryModelId == from);
        return x is null
            ? null
            : new WallGeometrySplat
            {
                GeometryModelId = to,
                StoredPath = x.StoredPath,
                SizeBytes = x.SizeBytes,
                MobileStoredPath = x.MobileStoredPath,
                MobileSizeBytes = x.MobileSizeBytes,
                SplatCount = x.SplatCount,
                LodLevelsJson = x.LodLevelsJson,
                UncleanedStoredPath = x.UncleanedStoredPath,
                UncleanedSizeBytes = x.UncleanedSizeBytes,
                FrameJson = WallGeometryModelTransformer.TransformSplatFrame(x.FrameJson, t),
            };
    }

    /// <summary>The capture now belongs to the new version; its follow-up record and coverage report are redone for it.</summary>
    private static async Task<Guid?> RepointCaptureAsync(BlocwerkDbContext db, Guid? captureId, Guid modelId)
    {
        var capture = captureId is { } id ? await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == id) : null;
        if (capture is null)
        {
            return null;
        }

        capture.GeometryModelId = modelId;
        capture.FollowUpJson = null;
        capture.CoverageJson = null;
        await db.SaveChangesAsync();
        return capture.Id;
    }
}
