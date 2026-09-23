// <copyright file="WallShapeRecognitionJob.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// One background run of the wall-update shape recognition: works out the targets, then per staged panel
/// decodes the staged photo ONCE and outlines its targets in small batches, writing a proposal per hold and
/// the progress counter after every batch. A hold that already has a proposal is skipped, so a restarted
/// run resumes where the last one stopped. Only proposal rows and the session's run fields are written.
/// </summary>
internal sealed class WallShapeRecognitionJob
{
    /// <summary>Holds per SaveChanges (and per progress tick).</summary>
    public const int BatchSize = 20;

    private readonly RootDbContextFactory factory;
    private readonly IHoldOutlineService outlines;
    private readonly ILogger logger;

    public WallShapeRecognitionJob(RootDbContextFactory factory, IHoldOutlineService outlines, ILogger logger)
    {
        this.factory = factory;
        this.outlines = outlines;
        this.logger = logger;
    }

    public async Task RunAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await RunCoreAsync(sessionId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Skipped (or the host is stopping): whoever cancelled owns the status.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shape recognition failed for wall update session {SessionId}", sessionId);
            await MarkFailedAsync(sessionId, ex.Message);
        }
    }

    /// <summary>Outlines one hold and turns the result into a proposal row (pure but for the outliner).</summary>
    internal static WallUpdateShapeProposal Propose(IHoldOutlineSession photo, Guid sessionId, Guid panelId, ShapeTarget target)
    {
        var seed = HoldOutlineUpgradePlanner.SeedFor(target.Hold);
        var result = photo.Outline(seed);
        var outcome = HoldOutlineUpgradePlanner.Classify(target.Hold, result, seed, photo.ImageWidth, photo.ImageHeight);
        var contour = outcome == HoldOutlineUpgradeOutcome.Outline;
        return new WallUpdateShapeProposal
        {
            SessionId = sessionId,
            HoldId = target.Hold.Id,
            PanelId = panelId,
            Reason = target.Reason,
            Method = contour ? result.Method : HoldOutlineMethod.CircleFallback,
            Confidence = contour ? result.Confidence : Math.Min(result.Confidence, 0.2),
            AnchorX = result.AnchorX,
            AnchorY = result.AnchorY,
            ImageWidth = photo.ImageWidth,
            ImageHeight = photo.ImageHeight,
            ShapeJson = contour ? ShapeJson.Write(result.ShapePoints) : null,
            HolesJson = contour ? ShapeJson.WriteRings(result.ShapeHoles) : null,
            PreviousShapeJson = ShapeJson.Write(target.PreviousShape),
        };
    }

    private async Task RunCoreAsync(Guid sessionId, CancellationToken ct)
    {
        await using var db = factory.CreateDbContext();
        db.CurrentUserId = Guid.Empty;
        var session = await db.WallUpdateSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.Status != WallUpdateSessionStatus.Open || session.ShapeStatus != ShapeRecognitionStatus.Running)
        {
            return;
        }

        var started = DateTimeOffset.UtcNow;
        var (targets, skippedManual) = await ShapeRecognitionTargets.LoadAsync(db, session, ct);
        var already = await db.WallUpdateShapeProposals.CountAsync(p => p.SessionId == sessionId, ct);
        session.ShapeTotal = already + targets.Count;
        session.ShapeDone = already;
        session.ShapeSkippedManual = skippedManual;
        await db.SaveChangesAsync(ct);

        foreach (var panel in targets.GroupBy(t => t.Hold.WallPanelId!.Value))
        {
            if (!await OutlinePanelAsync(db, session, panel.Key, panel.ToList(), ct))
            {
                return;
            }
        }

        session.ShapeStatus = ShapeRecognitionStatus.Completed;
        session.ShapeFinishedAt = DateTimeOffset.UtcNow;

        // A finished run is ready for review: advance the cursor as the wizard's own "Review" button would, so
        // an update driven over the API (nobody on the page) reopens on the review.
        if (session.Phase == WallUpdatePhase.Shapes)
        {
            WallUpdateSessions.MovePhase(
                session, WallUpdatePhase.ShapeReview, 0, session.LastActiveByUserId ?? session.CreatedByUserId ?? Guid.Empty);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Shape recognition for session {SessionId}: {Count} holds outlined in {Seconds:F1}s ({Manual} hand-drawn skipped)",
            sessionId, targets.Count, (DateTimeOffset.UtcNow - started).TotalSeconds, skippedManual);
    }

    /// <summary>Outlines one panel's targets. False when the session stopped being ours mid-run.</summary>
    private async Task<bool> OutlinePanelAsync(
        BlocwerkDbContext db, WallUpdateSession session, Guid panelId, List<ShapeTarget> targets, CancellationToken ct)
    {
        var bytes = await db.WallPanels.AsNoTracking()
            .Where(p => p.Id == panelId)
            .Select(p => p.StagedPhoto)
            .FirstOrDefaultAsync(ct);
        if (bytes is null || ImagePixelLimit.IsTooLarge(bytes))
        {
            logger.LogWarning("Shape recognition: staged photo of panel {PanelId} missing or too large; its holds keep their shapes", panelId);
            session.ShapeDone += targets.Count;
            await db.SaveChangesAsync(ct);
            return true;
        }

        using var photo = await Task.Run(() => outlines.OpenSession(bytes), ct);
        foreach (var batch in targets.Chunk(BatchSize))
        {
            var rows = await Task.Run(() => batch.Select(t => Propose(photo, session.Id, panelId, t)).ToList(), ct);
            await db.Entry(session).ReloadAsync(ct);
            if (session.Status != WallUpdateSessionStatus.Open || session.ShapeStatus != ShapeRecognitionStatus.Running)
            {
                return false;
            }

            db.WallUpdateShapeProposals.AddRange(rows);
            session.ShapeDone += rows.Count;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    private async Task MarkFailedAsync(Guid sessionId, string message)
    {
        try
        {
            await using var db = factory.CreateDbContext();
            db.CurrentUserId = Guid.Empty;
            var session = await db.WallUpdateSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session is null || session.ShapeStatus != ShapeRecognitionStatus.Running)
            {
                return;
            }

            session.ShapeStatus = ShapeRecognitionStatus.Failed;
            session.ShapeError = message.Length > 500 ? message[..500] : message;
            session.ShapeFinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the failed shape recognition of session {SessionId}", sessionId);
        }
    }
}
