// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.Projects;

/// <summary>The current user's project marks, stored and guarded like <see cref="BoulderFavorite"/>.</summary>
public sealed class BoulderProjectService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IKioskContext kioskContext,
    IBoulderFeedbackService feedbackService,
    ILogger<BoulderProjectService> logger) : IBoulderProjectService
{
    public async Task<bool> ToggleProjectAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderProject.Toggle");
        try
        {
            var current = await IsProjectAsync(boulderId);
            return await SetProjectAsync(boulderId, !current);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<bool> SetProjectAsync(Guid boulderId, bool project)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderProject.Set");
        try
        {
            var user = await SignedInUserAsync();
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            await BoulderMemberGuard.EnsureMemberAsync(db, boulderId, user.Id);

            var existing = await db.BoulderProjects
                .FirstOrDefaultAsync(p => p.BoulderId == boulderId && p.UserId == user.Id);
            if (project && existing == null)
            {
                db.BoulderProjects.Add(new BoulderProject { BoulderId = boulderId, UserId = user.Id });
            }
            else if (!project && existing != null)
            {
                db.BoulderProjects.Remove(existing);
            }
            else
            {
                return project;
            }

            await db.SaveChangesAsync();
            logger.LogInformation("Project set to {Project} on boulder {BoulderId} by {UserId}", project, boulderId, user.Id);
            return project;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<bool> IsProjectAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderProject.IsProject");
        try
        {
            var user = await SignedInUserAsync();
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            return await db.BoulderProjects.AnyAsync(p => p.BoulderId == boulderId && p.UserId == user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<BoulderListItem>> GetMyProjectsAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderProject.GetMine", wallId);
        try
        {
            var user = await SignedInUserAsync();
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            var markedAt = await db.BoulderProjects.AsNoTracking()
                .Where(p => p.UserId == user.Id && p.Boulder.WallId == wallId)
                .ToDictionaryAsync(p => p.BoulderId, p => p.CreatedAt);
            if (markedAt.Count == 0)
            {
                return [];
            }

            // The list read applies the wall's access rules and fills in the rest of each row.
            var items = await feedbackService.GetBoulderListAsync(wallId);
            return items.Where(i => markedAt.ContainsKey(i.Boulder.Id))
                .OrderByDescending(i => markedAt[i.Boulder.Id])
                .ToList();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    // A kiosk tablet is shared by everyone at the wall: the climber picked on it has no projects there, so its session
    // may neither read nor set them. Refused like a non-member, so a queued offline entry is dropped, not retried.
    private async Task<User> SignedInUserAsync()
    {
        if (kioskContext.IsKiosk)
        {
            throw new InvalidOperationException(BoulderMemberGuard.NotAMember);
        }

        return await currentUserService.GetCurrentUserAsync();
    }
}
