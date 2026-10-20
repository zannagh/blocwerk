// <copyright file="HoldMergeSplitService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldMergeSplitService"/>: the shared plumbing (who may, which holds are live, the photo's aspect, the
/// journal batch, undo). The merge and the split live in their own parts; the geometry in <see cref="HoldOutlineBoolean"/>.
/// </summary>
/// <param name="dbContextFactory">Context factory.</param>
/// <param name="currentUserService">The acting user.</param>
/// <param name="journal">Opens the named journal batch.</param>
/// <param name="reverter">Reverts a batch (undo).</param>
/// <param name="logger">Logger.</param>
/// <param name="kioskContext">The kiosk context, when the host has one.</param>
/// <param name="refinementQueue">Refreshes the 3D footprints of the changed holds in the background; optional.</param>
public sealed partial class HoldMergeSplitService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IChangeJournal journal,
    ChangeJournalReverter reverter,
    ILogger<HoldMergeSplitService> logger,
    IKioskContext? kioskContext = null,
    IHoldRefinementQueue? refinementQueue = null) : IHoldMergeSplitService
{
    /// <summary>The journal batch label of a merge.</summary>
    public const string MergeBatchLabel = "hold-tool-merge";

    /// <summary>The journal batch label of a split.</summary>
    public const string SplitBatchLabel = "hold-tool-split";

    private const string BusyMessage = "Another update of this wall's holds (a clean-up, an outline upgrade, a merge or a wall update) is running. Try again in a moment.";

    /// <inheritdoc/>
    public async Task<ChangeJournalRevertResult> UndoAsync(Guid wallId, Guid batchId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenAsync(wallId, "Undoing a merge or split", ct);
        await using (db)
        {
            var known = await db.ChangeJournalBatches.AsNoTracking()
                .AnyAsync(b => b.Id == batchId && (b.Label == MergeBatchLabel || b.Label == SplitBatchLabel) && b.ScopeId == wallId, ct);
            if (!known)
            {
                throw new UserFacingException("That merge or split was not found on this wall.");
            }

            using var wallLock = WallHoldWriteLock.TryAcquire(wallId, BusyMessage);
            var result = await reverter.RevertBatchAsync(batchId, force: false, ct);
            logger.LogInformation("Hold tool batch {BatchId} on wall {WallId} undone by {UserId}: {Reverted}", batchId, wallId, userId, result.Reverted);
            return result;
        }
    }

    /// <summary>A live, non-virtual hold of the wall, tracked.</summary>
    private static async Task<Hold> LoadLiveAsync(BlocwerkDbContext db, Guid wallId, Guid holdId, CancellationToken ct)
    {
        var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId && h.WallId == wallId, ct)
            ?? throw new UserFacingException("That hold no longer exists. Reload the wall.");
        if (hold.IsVirtual)
        {
            throw new UserFacingException("Virtual holds are placeholders without an outline: join them with the Join tool or make them actual first.");
        }

        if (!await IsLiveAsync(db, hold, ct))
        {
            throw new UserFacingException("That hold belongs to an earlier photo of the wall, not the live one.");
        }

        return hold;
    }

    /// <summary>On the newest photo of its panel (or, panel-less, on the wall's current generation).</summary>
    private static async Task<bool> IsLiveAsync(BlocwerkDbContext db, Hold hold, CancellationToken ct)
    {
        if (hold.WallPanelId is not { } panelId)
        {
            return await db.Walls.AnyAsync(w => w.Id == hold.WallId && w.CurrentGeneration == hold.Generation, ct);
        }

        var panel = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => new { p.Col, p.Row, p.Generation }).FirstOrDefaultAsync(ct);
        return panel is not null && panel.Generation == hold.Generation
            && !await db.WallPanels.AnyAsync(p => p.WallId == hold.WallId && p.Col == panel.Col && p.Row == panel.Row && p.Generation > panel.Generation, ct);
    }

    /// <summary>Width / height of the photo the hold sits on (header read only); 4:3 when it cannot be read.</summary>
    private static async Task<double> AspectAsync(BlocwerkDbContext db, Hold hold, CancellationToken ct)
    {
        var key = new Wall3DPhotoKey(hold.WallPanelId, hold.Generation);
        var infos = await PanelPhotoInfoLoader.LoadAsync(db, hold.WallId, [key], ct);
        return infos.TryGetValue(key, out var info) && info.Height > 0 && info.Width > 0 ? (double)info.Width / info.Height : 4.0 / 3.0;
    }

    private static HoldToolOutline ToOutline(OutlineRings rings) =>
        new([.. rings.Outer.Select(ToNormalized)], [.. rings.Holes.Select(h => (IReadOnlyList<NormalizedPoint>)[.. h.Select(ToNormalized)])]);

    private static NormalizedPoint ToNormalized(P2 p) => new(Math.Round(p.X, 5), Math.Round(p.Y, 5));

    /// <summary>Saves inside a named, wall-scoped batch (so the window export and replay pick it up) and returns its id.</summary>
    private async Task<Guid> SaveInBatchAsync(BlocwerkDbContext db, string label, Guid wallId, Guid? syncHoldId, CancellationToken ct)
    {
        using var scope = journal.BeginBatch(label, ChangeJournalScopeKind.Wall, wallId);
        var batchId = ((ChangeJournalBatchScope)scope).BatchId;
        await db.SaveChangesAsync(ct);

        // What the merge carried onto the kept hold reaches its linked twins in the same batch, so undo reverts both.
        if (syncHoldId is { } holdId && (await LinkedHoldSync.ReconcileAsync(db, wallId, holdId, ct)).Any)
        {
            await db.SaveChangesAsync(ct);
        }

        return batchId;
    }

    private void QueueFootprints(Guid wallId, params Guid[] holdIds) => refinementQueue?.Enqueue(wallId, holdIds);

    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenAsync(Guid wallId, string action, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, action);
            await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, ct);
            return (db, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
