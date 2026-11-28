// <copyright file="BoulderService.Variants.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Families of variants: a parent boulder and the boulders whose <see cref="Boulder.ParentBoulderId"/> names it.</summary>
public partial interface IBoulderService
{
    /// <summary>
    /// The rest of this boulder's published family: on a parent its variants, on a variant its parent first and then its
    /// sibling variants. Drafts and archived boulders are left out.
    /// </summary>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="shareToken">The wall's share token on the share path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The family; empty for a boulder without one, or one the viewer may not read.</returns>
    Task<IReadOnlyList<BoulderVariantView>> GetVariantsAsync(Guid boulderId, string? shareToken = null, CancellationToken ct = default);

    /// <summary>What the Mark Variant page lists for this boulder.</summary>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The choices, or null when the boulder is not readable.</returns>
    Task<BoulderVariantChoices?> GetVariantChoicesAsync(Guid boulderId, CancellationToken ct = default);

    /// <summary>
    /// Makes <paramref name="variantIds"/> the listed variants of this boulder, which stays or becomes a parent. A chosen
    /// boulder brings its own variants along; a listed variant left out loses its parent. Drafts, archived and historic
    /// variants are not listed and stay. On a boulder that is a variant itself the chosen boulders join its parent and the
    /// boulder stays where it is, unless the parent is among them: then the family turns around and this boulder leads it.
    /// Only the boulder's creator, a setter or a wall admin may do this.
    /// </summary>
    /// <param name="boulderId">The boulder the change starts from.</param>
    /// <param name="variantIds">The boulders that should be its variants.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes once the family is saved.</returns>
    Task SetVariantsAsync(Guid boulderId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct = default);

    /// <summary>
    /// Makes this boulder a variant of <paramref name="parentId"/>, or of that boulder's parent when it is a variant
    /// itself; the boulder's own variants move along. Null takes the boulder out of its family. Same permission as
    /// <see cref="SetVariantsAsync"/>.
    /// </summary>
    /// <param name="boulderId">The boulder the change starts from.</param>
    /// <param name="parentId">The boulder to put it under, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes once the family is saved.</returns>
    Task SetParentAsync(Guid boulderId, Guid? parentId, CancellationToken ct = default);
}

/// <summary>Reads and relinks families of variants.</summary>
public partial class BoulderService
{
    /// <summary>The refusal when a variant would be on another wall than its parent.</summary>
    public const string VariantOtherWallMessage = "A variant has to be on the same wall as its parent.";

    /// <summary>The refusal when somebody without edit rights changes a boulder's variants.</summary>
    public const string VariantPermissionMessage = "Only the creator, a setter, or a wall admin can change this boulder's variants.";

    /// <summary>The refusal when a boulder would move that the user may not edit, or out of a family they may not edit.</summary>
    public const string VariantTargetPermissionMessage = "You can only mark boulders as variants that you may edit, together with their family.";

    /// <summary>The refusal when a draft, archived or historic boulder is linked.</summary>
    public const string VariantNotLiveMessage = "Only published boulders that are still on the wall can be marked as variants.";

    /// <summary>The refusal when a boulder would end up as a variant of itself.</summary>
    public const string VariantCycleMessage = "A boulder can't be a variant of itself or of one of its own variants.";

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoulderVariantView>> GetVariantsAsync(Guid boulderId, string? shareToken = null, CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetVariants");
        try
        {
            var viewerId = shareToken is null ? await ResolveViewerIdAsync() : Guid.Empty;
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = viewerId;

            var readable = shareToken is null
                ? db.Boulders.Where(b => db.Walls.Any(w => w.Id == b.WallId) && (viewerId != Guid.Empty || !b.IsDraft))
                : db.Boulders.Where(b => b.Wall.ShareToken == shareToken && !b.IsDraft);
            var self = await readable
                .Where(b => b.Id == boulderId)
                .Select(b => new { b.WallId, b.ParentBoulderId })
                .FirstOrDefaultAsync(ct);
            if (self is null)
            {
                return [];
            }

            var parentId = self.ParentBoulderId ?? boulderId;
            var family = await db.Boulders.AsNoTracking()
                .Where(b => b.WallId == self.WallId && b.Id != boulderId && !b.IsDraft && !b.IsArchived
                    && (b.Id == parentId || b.ParentBoulderId == parentId))
                .Select(b => new BoulderVariantView(b.Id, b.Name, b.Grade, b.IsHistoric, b.Id == parentId))
                .ToListAsync(ct);
            return family.OrderByDescending(v => v.IsParent).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<BoulderVariantChoices?> GetVariantChoicesAsync(Guid boulderId, CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetVariantChoices");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = user.Id;

            var start = await db.Boulders.AsNoTracking()
                .Include(b => b.Setters)
                .FirstOrDefaultAsync(b => b.Id == boulderId && db.Walls.Any(w => w.Id == b.WallId), ct);
            if (start is null)
            {
                return null;
            }

            var rows = await db.Boulders.AsNoTracking()
                .Where(b => b.WallId == start.WallId && b.Id != boulderId)
                .Select(b => new VariantRow(
                    b.Id, b.Name, b.Grade, b.ParentBoulderId, !b.IsDraft && !b.IsArchived && !b.IsHistoric,
                    b.CreatedByUserId == user.Id || b.Setters.Any(s => s.UserId == user.Id)))
                .ToListAsync(ct);
            var isAdmin = await WallAdminGuard.IsWallAdminAsync(db, start.WallId, user.Id, ct);
            return BuildChoices(start, rows, await CanManageBoulderAsync(db, start, user.Id), isAdmin);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SetVariantsAsync(Guid boulderId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.SetVariants");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = user.Id;

            var start = await LoadEditableVariantStartAsync(db, boulderId, user.Id, ct);
            if (variantIds.Contains(boulderId))
            {
                throw new UserFacingException(VariantCycleMessage);
            }

            var ids = variantIds.Distinct().ToList();
            var targets = await db.Boulders.Include(b => b.Setters).Where(b => ids.Contains(b.Id)).ToListAsync(ct);
            EnsureLinkable(start, targets, ids.Count);

            // A variant has no variants of its own: what it gathers joins its parent, unless that parent is what it picked.
            var keepsParent = start.ParentBoulderId is { } current && !ids.Contains(current);
            var parentId = keepsParent ? start.ParentBoulderId!.Value : boulderId;
            await EnsureMayMoveAsync(db, targets, parentId, user.Id, ct);

            var affected = ids.Append(boulderId).ToList();
            var variants = await db.Boulders.Where(b => b.ParentBoulderId != null && affected.Contains(b.ParentBoulderId.Value)).ToListAsync(ct);
            foreach (var dropped in variants.Where(v => v.ParentBoulderId == boulderId && !ids.Contains(v.Id) && IsLiveBoulder(v)))
            {
                dropped.ParentBoulderId = null;
            }

            if (targets.Count > 0)
            {
                start.ParentBoulderId = keepsParent ? parentId : null;
                foreach (var moved in variants.Where(v => v.ParentBoulderId is { } p && ids.Contains(p) && v.Id != boulderId).Concat(targets))
                {
                    moved.ParentBoulderId = parentId;
                }
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Boulder {BoulderId} given {VariantCount} variants by {UserId}", boulderId, targets.Count, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SetParentAsync(Guid boulderId, Guid? parentId, CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.SetParent");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = user.Id;

            var start = await LoadEditableVariantStartAsync(db, boulderId, user.Id, ct);
            if (parentId is not { } id)
            {
                start.ParentBoulderId = null;
                await db.SaveChangesAsync(ct);
                return;
            }

            var picked = await db.Boulders.FirstOrDefaultAsync(b => b.Id == id, ct);
            var parent = picked?.ParentBoulderId is { } grandparentId
                ? await db.Boulders.FirstOrDefaultAsync(b => b.Id == grandparentId, ct)
                : picked;
            if (id == boulderId || parent?.Id == boulderId)
            {
                throw new UserFacingException(VariantCycleMessage);
            }

            EnsureLinkable(start, parent is null ? [] : [parent], 1);
            var ownVariants = await db.Boulders.Where(b => b.ParentBoulderId == boulderId).ToListAsync(ct);
            foreach (var moved in ownVariants.Append(start))
            {
                moved.ParentBoulderId = parent!.Id;
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Boulder {BoulderId} made a variant of {ParentId} by {UserId}", boulderId, parent!.Id, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Refuses before anything moves unless the user may edit every chosen boulder that changes parent, and the parent of
    /// every family one of them is taken out of. Rights on the boulder the change starts from cover only that boulder.
    /// </summary>
    private async Task EnsureMayMoveAsync(BlocwerkDbContext db, List<Boulder> targets, Guid parentId, Guid userId, CancellationToken ct)
    {
        var moving = targets.Where(t => t.ParentBoulderId != parentId).ToList();
        var formerParentIds = moving.Select(t => t.ParentBoulderId).OfType<Guid>().Distinct().ToList();
        var formerParents = await db.Boulders.AsNoTracking().Include(b => b.Setters).Where(b => formerParentIds.Contains(b.Id)).ToListAsync(ct);
        foreach (var boulder in moving.Concat(formerParents))
        {
            if (!await CanManageBoulderAsync(db, boulder, userId))
            {
                _logger.LogWarning("Variant change refused: user {UserId} may not move boulder {BoulderId}", userId, boulder.Id);
                throw new UserFacingException(VariantTargetPermissionMessage);
            }
        }
    }

    private static bool IsLiveBoulder(Boulder b) => b is { IsDraft: false, IsArchived: false, IsHistoric: false };

    private static BoulderVariantChoices BuildChoices(Boulder start, List<VariantRow> rows, bool canEdit, bool isAdmin)
    {
        var names = rows.ToDictionary(r => r.Id, r => r.Name);
        var mine = rows.Where(r => isAdmin || r.IsMine).Select(r => r.Id).ToHashSet();
        bool MayMove(VariantRow r) => mine.Contains(r.Id) && (r.ParentId is not { } p || p == start.ParentBoulderId || mine.Contains(p));
        var counts = rows.Where(r => r.ParentId is not null && r.IsLive).GroupBy(r => r.ParentId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var choices = rows.Where(r => r.IsLive)
            .Select(r => new BoulderVariantChoice(
                r.Id,
                r.Name,
                r.Grade,
                r.ParentId == start.Id,
                r.Id == start.ParentBoulderId,
                start.ParentBoulderId is not null && r.ParentId == start.ParentBoulderId,
                MayMove(r),
                counts.GetValueOrDefault(r.Id),
                r.ParentId is { } parent && parent != start.Id ? names.GetValueOrDefault(parent) : null))
            .OrderByDescending(c => c.IsParent)
            .ThenByDescending(c => c.IsVariant)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BoulderVariantChoices(start.Name, canEdit, start.ParentBoulderId, choices, rows.Count(r => r.ParentId == start.Id && !r.IsLive));
    }

    /// <summary>Loads the boulder a family change starts from, refusing anybody who may not edit it.</summary>
    private async Task<Boulder> LoadEditableVariantStartAsync(BlocwerkDbContext db, Guid boulderId, Guid userId, CancellationToken ct)
    {
        var start = await db.Boulders
            .Include(b => b.Setters)
            .FirstOrDefaultAsync(b => b.Id == boulderId && db.Walls.Any(w => w.Id == b.WallId), ct)
            ?? throw new InvalidOperationException("Boulder not found");
        if (!await CanManageBoulderAsync(db, start, userId))
        {
            _logger.LogWarning("Variant change denied: user {UserId} may not edit boulder {BoulderId}", userId, boulderId);
            throw new UserFacingException(VariantPermissionMessage);
        }

        return start;
    }

    /// <summary>Refuses a link to a boulder that is missing, on another wall, or not on the wall any more.</summary>
    private static void EnsureLinkable(Boulder start, IReadOnlyCollection<Boulder> targets, int expected)
    {
        if (targets.Count != expected || targets.Any(t => t.WallId != start.WallId))
        {
            throw new UserFacingException(VariantOtherWallMessage);
        }

        if (expected > 0 && !targets.Append(start).All(IsLiveBoulder))
        {
            throw new UserFacingException(VariantNotLiveMessage);
        }
    }

    /// <summary>Puts a new boulder under its origin, or the origin's parent, and returns the holds the origin uses.</summary>
    private async Task<IReadOnlyCollection<Guid>> AttachToOriginAsync(BlocwerkDbContext db, Boulder variant, Guid? originId)
    {
        if (originId is not { } id)
        {
            return [];
        }

        // Through the wall filter: an origin on a wall the setter is not a member of reads as missing, so the answer does
        // not tell such a boulder exists. Only an origin the setter can see on another of their walls is refused.
        var origin = await db.Boulders.AsNoTracking()
            .Where(b => b.Id == id && db.Walls.Any(w => w.Id == b.WallId))
            .Select(b => new { b.WallId, b.IsDraft, b.ParentBoulderId })
            .FirstOrDefaultAsync();
        if (origin is not null && origin.WallId != variant.WallId)
        {
            throw new UserFacingException(VariantOtherWallMessage);
        }

        if (origin is null || origin.IsDraft)
        {
            // Gone while the create waited in the offline queue (or never visible): the setter's boulder still counts, without the link.
            _logger.LogWarning("Boulder {BoulderId} created without its origin {OriginId}, which is gone or a draft", variant.Id, id);
            return [];
        }

        variant.ParentBoulderId = origin.ParentBoulderId ?? id;
        return await db.BoulderHolds.AsNoTracking().Where(h => h.BoulderId == id).Select(h => h.HoldId).ToListAsync();
    }

    private sealed record VariantRow(Guid Id, string Name, string? Grade, Guid? ParentId, bool IsLive, bool IsMine);
}
