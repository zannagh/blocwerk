// <copyright file="HoldLinkSuggestionService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.HoldLinks;

/// <summary>
/// <see cref="IHoldLinkSuggestionService"/>. Besides the capture chain, a refresh runs whenever an admin asks for the
/// count or the list, so edits and new links since the last capture count; it is skipped when nothing changed
/// (<see cref="HoldLinkInputs.Fingerprint"/>). Writes only <see cref="HoldLinkSuggestion"/> rows; linking goes
/// through <see cref="IWallPanelService.CreateHoldLinkAsync"/> after the pair is checked again under the wall's lock.
/// </summary>
public sealed partial class HoldLinkSuggestionService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IWallPanelService panels,
    ILogger<HoldLinkSuggestionService> logger,
    IKioskContext? kioskContext = null) : IHoldLinkSuggestionService
{
    /// <inheritdoc />
    public async Task<int?> RefreshFromPipelineAsync(Guid wallId, CancellationToken ct = default)
    {
        try
        {
            return await RefreshAsync(wallId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Hold link suggestions on wall {WallId} failed", wallId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CanReviewAsync(Guid wallId, CancellationToken ct = default)
    {
        try
        {
            return kioskContext?.IsKiosk != true && await IsAdminAsync(wallId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> CountPendingAsync(Guid wallId, CancellationToken ct = default)
    {
        if (!await CanReviewAsync(wallId, ct))
        {
            return 0;
        }

        try
        {
            return await RefreshAsync(wallId, ct) ?? 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not count the hold link suggestions of wall {WallId}", wallId);
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HoldLinkSuggestionView>> ListAsync(Guid wallId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        if (await RefreshAsync(wallId, ct) is not > 0)
        {
            return [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await ViewsAsync(db, wallId, ct);
    }

    /// <inheritdoc />
    public async Task LinkAsync(Guid wallId, Guid holdAId, Guid holdBId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        var (a, b) = HoldLinkPairSuggestion.Key(holdAId, holdBId);
        await UnderGateAsync(
            wallId,
            async () =>
            {
                await EnsureStillLinkableAsync(wallId, a, b, ct);
                await panels.CreateHoldLinkAsync(wallId, a, b);
                await using var db = await dbContextFactory.CreateDbContextAsync(ct);
                await db.HoldLinkSuggestions
                    .Where(s => s.WallId == wallId && s.HoldAId == a && s.HoldBId == b && s.Status == HoldLinkSuggestionStatus.Pending)
                    .ExecuteDeleteAsync(ct);
            },
            ct);
        logger.LogInformation("Hold link suggestion {HoldA} / {HoldB} on wall {WallId} linked", a, b, wallId);
    }

    /// <inheritdoc />
    public async Task RejectAsync(Guid wallId, Guid holdAId, Guid holdBId, CancellationToken ct = default)
    {
        var userId = await EnsureAdminAsync(wallId, ct);
        var (a, b) = HoldLinkPairSuggestion.Key(holdAId, holdBId);
        await UnderGateAsync(wallId, () => StoreRejectionAsync(wallId, a, b, userId, ct), ct);
    }

    /// <summary>Refuses a link whose suggestion is gone or whose holds changed so that linking them is no longer right.</summary>
    private async Task EnsureStillLinkableAsync(Guid wallId, Guid a, Guid b, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var pending = await db.HoldLinkSuggestions.AnyAsync(
            s => s.WallId == wallId && s.HoldAId == a && s.HoldBId == b && s.Status == HoldLinkSuggestionStatus.Pending, ct);
        if (!pending)
        {
            throw new UserFacingException("This suggestion is out of date. Close the list and open it again.");
        }

        var inputs = await HoldLinkCandidateLoader.LoadAsync(db, wallId, ct);
        if (inputs is null || !inputs.PanelOf.TryGetValue(a, out var panelA) || !inputs.PanelOf.TryGetValue(b, out var panelB))
        {
            throw new UserFacingException("One of these holds is no longer on the wall photos. Close the list and open it again.");
        }

        var groups = new LinkGroups(inputs.PanelOf, await HoldLinkCandidateLoader.LinksAsync(db, wallId, ct));
        if (!groups.MayLink(a, panelA, b, panelB))
        {
            throw new UserFacingException(
                "These holds are already linked, or one of them is already linked to another hold on the other photo.");
        }
    }

    private async Task StoreRejectionAsync(Guid wallId, Guid a, Guid b, Guid userId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        if (a == b || await db.Holds.CountAsync(h => (h.Id == a || h.Id == b) && h.WallId == wallId, ct) != 2)
        {
            throw new UserFacingException("Those holds are not on this wall any more.");
        }

        var row = await db.HoldLinkSuggestions.FirstOrDefaultAsync(s => s.WallId == wallId && s.HoldAId == a && s.HoldBId == b, ct);
        if (row is null)
        {
            row = new HoldLinkSuggestion { WallId = wallId, HoldAId = a, HoldBId = b };
            db.HoldLinkSuggestions.Add(row);
        }

        row.Status = HoldLinkSuggestionStatus.Rejected;
        row.ReviewedAt = DateTimeOffset.UtcNow;
        row.ReviewedByUserId = userId;
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> IsAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        return await WallAdminGuard.IsWallAdminAsync(db, wallId, user.Id, ct);
    }

    private async Task<Guid> EnsureAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Linking holds across photos");
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
        return user.Id;
    }
}
