// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Solving a finished capture's 3D model again from its kept photos: only the mark on <see cref="WallCapture.SolveJobId"/>
/// is written here and <see cref="WallModelResolveWorker"/> runs it (<see cref="WallCaptureProcessor.ResolveModelAsync"/>).
/// The photo-real view is kept (never trained again).
/// </summary>
public sealed partial class WallCaptureService
{
    public async Task<IReadOnlyList<string>> ResolveModelAsync(Guid captureId)
    {
        if (!IsComputeConfigured)
        {
            return ["No 3D computation service is configured on this server."];
        }

        var (db, userId, capture) = await OpenCaptureAsync(captureId);
        await using (db)
        {
            var problems = await ResolveProblemsAsync(db, capture);
            if (problems.Count > 0)
            {
                return problems;
            }

            capture.SolveJobId = CaptureResolveMark.Mark;
            await db.SaveChangesAsync();
            resolveQueue?.Enqueue(capture.Id);
            logger.LogInformation("3D model of capture {CaptureId} queued to be solved again by {UserId}", capture.Id, userId);
            return [];
        }
    }

    /// <summary>A finished marker capture (not while its photo-real stage runs: that view is stored on its model).</summary>
    internal static bool MayResolveModel(WallCapture capture) => capture.GeometryMode == WallCaptureGeometryMode.Markers
        && capture.Status is WallCaptureStatus.Succeeded or WallCaptureStatus.SucceededWithoutTextures or WallCaptureStatus.SucceededWithoutSplat
        && !CaptureResolveMark.IsResolving(capture.SolveJobId) && !CaptureTextureOutcome.IsRerendering(capture.TexturesJobId);

    private static async Task<List<string>> ResolveProblemsAsync(Data.BlocwerkDbContext db, WallCapture capture)
    {
        if (CaptureResolveMark.IsResolving(capture.SolveJobId))
        {
            return ["The 3D model of this capture is already being solved again."];
        }

        if (!MayResolveModel(capture))
        {
            return ["Only a finished marker capture (not while its wall textures are rendered again) can have its 3D model solved again."];
        }

        var errors = new List<string>();
        var modelId = capture.GeometryModelId;
        if (modelId is null || !await db.WallGeometryModels.AnyAsync(m => m.Id == modelId && m.WallId == capture.WallId && m.IsActive))
        {
            errors.Add("This capture's 3D model is not the wall's active one.");
        }

        if (await db.WallCapturePhotos.CountAsync(p => p.CaptureId == capture.Id) < 2)
        {
            errors.Add("This capture's photos were already deleted (they are kept for a limited time). Start a new capture.");
        }

        // A view still to be installed would land on the model it was queued for, not on the new one.
        if ((await Runners.GpuJobText.PendingAsync(db, [capture.Id])).ContainsKey(capture.Id))
        {
            errors.Add("This capture's photo-real view is still waiting for (or training on) a 3D runner. Wait for it to be installed.");
        }

        if (await db.WallCaptures.AnyAsync(c => c.WallId == capture.WallId && c.Id != capture.Id && (c.Status == WallCaptureStatus.Queued
            || c.Status == WallCaptureStatus.Detecting || c.Status == WallCaptureStatus.Solving || c.Status == WallCaptureStatus.Texturing
            || c.Status == WallCaptureStatus.Splatting)))
        {
            errors.Add("Another capture of this wall is still being processed. Wait for it to finish.");
        }

        // The new model is imported as the capture's creator.
        if (!await WallAdminGuard.IsWallAdminAsync(db, capture.WallId, capture.CreatedByUserId, CancellationToken.None))
        {
            errors.Add("The admin who started this capture no longer administers the wall; start a new capture instead.");
        }

        return errors;
    }
}
