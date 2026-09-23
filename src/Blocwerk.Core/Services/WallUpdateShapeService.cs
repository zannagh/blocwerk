// <copyright file="WallUpdateShapeService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IWallUpdateShapeService"/>: the run half here (start, status, skip), the review half in the
/// Review partial. The UI wizard and the REST controller both call exactly these methods, so the two can
/// never disagree on who may do what.
/// </summary>
public sealed partial class WallUpdateShapeService : IWallUpdateShapeService
{
    private const string KioskRefusal = "Recognising hold shapes";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly WallShapeRecognitionRunner runner;
    private readonly ILogger<WallUpdateShapeService> logger;
    private readonly IKioskContext? kioskContext;

    public WallUpdateShapeService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        WallShapeRecognitionRunner runner,
        ILogger<WallUpdateShapeService> logger,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.runner = runner;
        this.logger = logger;
        this.kioskContext = kioskContext;
    }

    /// <inheritdoc/>
    public async Task<ShapeRecognitionStatusInfo> StartRecognitionAsync(
        Guid wallId, ShapeRecognitionOptions options, Guid? expectedSessionId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!runner.Available)
        {
            throw new UserFacingException("Outline detection is switched off on this server.");
        }

        var (db, userId, session) = await OpenAsync(wallId, expectedSessionId, ct);
        await using (db)
        {
            if (session.ShapeStatus == ShapeRecognitionStatus.Running && runner.IsRunning(session.Id))
            {
                return await DescribeAsync(db, session, ct);
            }

            if (options.Rerun)
            {
                await db.WallUpdateShapeProposals.Where(p => p.SessionId == session.Id).ExecuteDeleteAsync(ct);
            }

            session.ShapeStatus = ShapeRecognitionStatus.Running;
            session.ShapeScope = options.Scope;
            session.ShapeOverwriteManual = options.OverwriteManual;
            session.ShapeStartedAt = DateTimeOffset.UtcNow;
            session.ShapeFinishedAt = null;
            session.ShapeError = null;
            WallUpdateSessions.MovePhase(session, WallUpdatePhase.Shapes, 0, userId);
            await db.SaveChangesAsync(ct);

            runner.Launch(session.Id);
            logger.LogInformation(
                "Shape recognition started on wall {WallId} (session {SessionId}) by {UserId}: {Scope}, overwriteManual={Manual}, rerun={Rerun}",
                wallId, session.Id, userId, options.Scope, options.OverwriteManual, options.Rerun);
            return await DescribeAsync(db, session, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<ShapeRecognitionStatusInfo> GetStatusAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _, session) = await OpenAsync(wallId, null, ct);
        await using (db)
        {
            return await DescribeAsync(db, session, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<ShapeRecognitionStatusInfo> SkipAsync(Guid wallId, Guid? expectedSessionId = null, CancellationToken ct = default)
    {
        var (db, userId, session) = await OpenAsync(wallId, expectedSessionId, ct);
        await using (db)
        {
            // Stop the run first and wait for it, so its last batch cannot land after the skip.
            runner.Cancel(session.Id);
            await runner.WhenIdleAsync(session.Id);
            await db.Entry(session).ReloadAsync(ct);
            session.ShapeStatus = ShapeRecognitionStatus.Skipped;
            session.ShapeFinishedAt = DateTimeOffset.UtcNow;
            WallUpdateSessions.MovePhase(session, WallUpdatePhase.Confirm, 0, userId);
            await db.SaveChangesAsync(ct);
            return await DescribeAsync(db, session, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<ShapeRecognitionStatusInfo> CompleteReviewAsync(
        Guid wallId, Guid? expectedSessionId = null, CancellationToken ct = default)
    {
        var (db, userId, session) = await OpenAsync(wallId, expectedSessionId, ct);
        await using (db)
        {
            if (session.ShapeStatus is not (ShapeRecognitionStatus.Completed or ShapeRecognitionStatus.Skipped))
            {
                throw new UserFacingException(
                    $"The shape recognition is {session.ShapeStatus}; finish or skip it before moving on.");
            }

            WallUpdateSessions.MovePhase(session, WallUpdatePhase.Confirm, 0, userId);
            await db.SaveChangesAsync(ct);
            return await DescribeAsync(db, session, ct);
        }
    }

    /// <summary>
    /// A context for a wall admin on a non-kiosk session, with the wall's open update — and, when the caller
    /// names one, only if that is still the open update (the stale-tab guard).
    /// </summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId, WallUpdateSession Session)> OpenAsync(
        Guid wallId, Guid? expectedSessionId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, KioskRefusal);
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
            await WallUpdateSessions.EnsureCurrentAsync(db, wallId, expectedSessionId, ct);
            var session = await WallUpdateSessions.FindOpenAsync(db, wallId, ct)
                ?? throw new UserFacingException("No in-flight wall update on this wall.");
            return (db, user.Id, session);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private async Task<ShapeRecognitionStatusInfo> DescribeAsync(BlocwerkDbContext db, WallUpdateSession session, CancellationToken ct)
    {
        var tally = (await db.WallUpdateShapeProposals.AsNoTracking()
                .Where(p => p.SessionId == session.Id)
                .GroupBy(p => p.Decision)
                .Select(g => new { Decision = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Decision, x => x.Count);
        return new ShapeRecognitionStatusInfo(
            session.Id,
            session.Phase,
            runner.Available,
            session.ShapeStatus,
            session.ShapeStatus == ShapeRecognitionStatus.Running && !runner.IsRunning(session.Id),
            session.ShapeScope,
            session.ShapeOverwriteManual,
            session.ShapeTotal,
            session.ShapeDone,
            session.ShapeSkippedManual,
            session.ShapeStartedAt,
            session.ShapeFinishedAt,
            session.ShapeError,
            tally);
    }
}
