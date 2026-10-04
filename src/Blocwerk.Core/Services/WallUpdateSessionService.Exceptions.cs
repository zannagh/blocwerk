// <copyright file="WallUpdateSessionService.Exceptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The confirm screen's cards of the session (<see cref="WallUpdateException"/>): written with the default decisions,
/// and read back for the page and for what Apply promotes.
/// </summary>
public partial class WallUpdateSessionService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<UpdateExceptionInfo>> GetUpdateExceptionsAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var session = await WallUpdateSessions.FindOpenAsync(db, wallId);
        return session is null ? [] : await ReadExceptionsAsync(db, session.Id);
    }

    /// <summary>The session's cards, in a stable order (kind, then the matcher's least sure first).</summary>
    internal static async Task<IReadOnlyList<UpdateExceptionInfo>> ReadExceptionsAsync(BlocwerkDbContext db, Guid sessionId)
    {
        var rows = await db.WallUpdateExceptions.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .Select(e => new
            {
                Row = e,
                OldPlaced = db.Holds.Any(h => h.Id == e.OldHoldId && h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null
                    && db.WallGeometryTextures.Any(t => t.FacetId == h.FacetId && t.GeometryModel.IsActive && t.GeometryModel.WallId == h.WallId)),
            })
            .ToListAsync();
        return rows
            .OrderBy(r => r.Row.Kind)
            .ThenBy(r => r.Row.Confidence ?? 0)
            .ThenBy(r => r.Row.Id)
            .Select(r => new UpdateExceptionInfo(
                r.Row.Id, r.Row.Kind, r.Row.OldHoldId, r.Row.StagedHoldId, r.Row.Status, r.Row.PhotoScore, r.Row.TextureScore,
                r.Row.Confidence, r.Row.FacetId is not null || (r.Row.Kind == UpdateExceptionKind.LowConfidenceMatch && r.OldPlaced)))
            .ToList();
    }

    /// <summary>Replaces the session's cards with <paramref name="cards"/> (not saved; the caller saves).</summary>
    private static async Task ReplaceExceptionsAsync(BlocwerkDbContext db, WallUpdateSession session, IReadOnlyList<UpdateExceptionDraft> cards)
    {
        var existing = await db.WallUpdateExceptions.Where(e => e.SessionId == session.Id).ToListAsync();
        db.WallUpdateExceptions.RemoveRange(existing);

        // A hold deleted from the staging meanwhile would break the FK, and its card would have cascaded away anyway.
        var ids = cards.SelectMany(c => new[] { c.OldHoldId, c.StagedHoldId }).OfType<Guid>().Distinct().ToList();
        var live = await LoadLiveHoldIdsAsync(db, session.WallId, ids);
        foreach (var c in cards.Where(c => c.OldHoldId is not { } o || live.Contains(o)).Where(c => c.StagedHoldId is not { } s || live.Contains(s)))
        {
            db.WallUpdateExceptions.Add(new WallUpdateException
            {
                SessionId = session.Id,
                Kind = c.Kind,
                OldHoldId = c.OldHoldId,
                StagedHoldId = c.StagedHoldId,
                PanelId = c.PanelId,
                X = c.X,
                Y = c.Y,
                GeometryModelId = c.ModelId,
                FacetId = c.FacetId,
                A = c.A,
                B = c.B,
                PhotoScore = c.PhotoScore,
                TextureScore = c.TextureScore,
                Confidence = c.Confidence,
            });
        }
    }
}
