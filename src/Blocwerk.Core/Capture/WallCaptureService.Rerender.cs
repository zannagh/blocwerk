// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Rendering a capture's wall textures again: only the marker on <see cref="WallCapture.TexturesJobId"/> is written here
/// (status, stage, error and the photo-real state stay as they are) and <see cref="WallTextureRerenderWorker"/> runs the
/// textures job (<see cref="WallCaptureProcessor.RerenderTexturesAsync"/>).
/// </summary>
public sealed partial class WallCaptureService
{
    public async Task<IReadOnlyList<string>> RerenderTexturesAsync(Guid captureId, TextureQuality quality = TextureQuality.Standard)
    {
        if (!IsComputeConfigured)
        {
            return ["No 3D computation service is configured on this server."];
        }

        if (!Enum.IsDefined(quality))
        {
            return ["Unknown texture quality."];
        }

        var (db, userId, capture) = await OpenCaptureAsync(captureId);
        await using (db)
        {
            var problems = await RerenderProblemsAsync(db, capture);
            if (problems.Count > 0)
            {
                return problems;
            }

            capture.TexturesJobId = CaptureTextureOutcome.Mark(quality);
            await db.SaveChangesAsync();
            textureQueue?.Enqueue(capture.Id);
            logger.LogInformation("Textures of capture {CaptureId} queued to be rendered again ({Quality}) by {UserId}", capture.Id, quality, userId);
            return [];
        }
    }

    public async Task<IReadOnlyList<TextureQualityEstimate>> EstimateTextureQualitiesAsync(Guid captureId)
    {
        var (db, _, capture) = await OpenCaptureAsync(captureId);
        await using (db)
        {
            if (capture.GeometryModelId is not { } modelId)
            {
                return [];
            }

            var stored = await db.WallGeometryModels.AsNoTracking().Where(m => m.Id == modelId).Select(m => m.Json).FirstOrDefaultAsync();
            if (stored is null)
            {
                return [];
            }

            // As SubmitTexturesAsync: the facets carried over from an earlier model are not rendered again.
            var geometry = RegisteredGeometry.WithoutFacets(stored, RegisteredGeometry.Carried(stored).CarriedFacets);
            var photos = await db.WallCapturePhotos.CountAsync(p => p.CaptureId == capture.Id);
            return TextureQualityEstimate.ForAll(geometry, photos, settings?.GeometryTextures ?? new GeometryTextureSettings());
        }
    }

    /// <summary>A capture whose model is live: finished with it, or on to the photo-real stage.</summary>
    internal static bool MayRerenderTextures(WallCaptureStatus status) => status is WallCaptureStatus.Succeeded
        or WallCaptureStatus.SucceededWithoutTextures or WallCaptureStatus.SucceededWithoutSplat or WallCaptureStatus.Splatting;

    private static async Task<List<string>> RerenderProblemsAsync(Data.BlocwerkDbContext db, WallCapture capture)
    {
        if (!MayRerenderTextures(capture.Status))
        {
            return ["Only a capture whose 3D model is ready can have its wall textures rendered again."];
        }

        var errors = new List<string>();
        if (CaptureTextureOutcome.IsRerendering(capture.TexturesJobId))
        {
            errors.Add("The wall textures of this capture are already being rendered again.");
        }

        if (CaptureResolveMark.IsResolving(capture.SolveJobId))
        {
            errors.Add("The 3D model of this capture is being solved again; its textures are rendered again after that.");
        }

        var modelId = capture.GeometryModelId;
        if (modelId is null || !await db.WallGeometryModels.AnyAsync(m => m.Id == modelId && m.WallId == capture.WallId && m.IsActive))
        {
            errors.Add("This capture's 3D model is not the wall's active one.");
        }

        if (!await db.WallCapturePhotos.AnyAsync(p => p.CaptureId == capture.Id))
        {
            errors.Add("This capture's photos were already deleted (they are kept for a limited time). Start a new capture.");
        }

        // The holds are placed on the new textures as the capture's creator.
        if (!await WallAdminGuard.IsWallAdminAsync(db, capture.WallId, capture.CreatedByUserId, CancellationToken.None))
        {
            errors.Add("The admin who started this capture no longer administers the wall; start a new capture instead.");
        }

        return errors;
    }
}
