// <copyright file="PanelCropService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>
/// <see cref="IPanelCropService"/>: crops edit the live panel row in place rather than starting a new generation. A crop
/// changes the frame, not the wall: no hold is physically different, so the generation/lineage machinery of a wall update
/// (new panel row, cloned holds, carry links, boulder review) has nothing to record. The original is kept beside the
/// panel (<see cref="WallPanelCrop"/>) and <see cref="WallPanel.PhotoRevision"/> moves so every cache keyed on the photo
/// (ETag, image variants, 3D registration) sees a new photo. Re-mapping lives in <c>PanelCropService.Remap.cs</c>.
/// </summary>
public sealed partial class PanelCropService : IPanelCropService
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly ILogger<PanelCropService> logger;
    private readonly IKioskContext? kioskContext;
    private readonly IChangeJournal? changeJournal;
    private readonly IHoldRefinementQueue? refinementQueue;
    private readonly ChangeJournalReverter? reverter;

    /// <summary>Initializes a new instance of the <see cref="PanelCropService"/> class. The optional parts are absent in unit tests and tool hosts, as on <c>WallPanelService</c>.</summary>
    public PanelCropService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        ILogger<PanelCropService> logger,
        IKioskContext? kioskContext = null,
        IChangeJournal? changeJournal = null,
        IHoldRefinementQueue? refinementQueue = null,
        ChangeJournalReverter? reverter = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.logger = logger;
        this.kioskContext = kioskContext;
        this.changeJournal = changeJournal;
        this.refinementQueue = refinementQueue;
        this.reverter = reverter;
    }

    /// <inheritdoc/>
    public async Task<PanelCropState?> GetStateAsync(Guid wallId, Guid panelId, CancellationToken ct = default)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        var panel = await db.WallPanels.AsNoTracking()
            .Where(p => p.Id == panelId && p.WallId == wallId && p.Photo != null)
            .Select(p => new { p.PhotoRevision })
            .FirstOrDefaultAsync(ct);
        if (panel is null)
        {
            return null;
        }

        var crop = await db.WallPanelCrops.AsNoTracking()
            .Where(c => c.WallPanelId == panelId)
            .Select(c => new { c.Left, c.Top, c.Width, c.Height })
            .FirstOrDefaultAsync(ct);
        var rect = crop is null ? (PanelCropRect?)null : new PanelCropRect(crop.Left, crop.Top, crop.Width, crop.Height);
        var removed = crop is null ? 0 : (await CropChainAsync(db, wallId, panelId, ct)).Sum(b => b.RemovedHolds);
        return new PanelCropState(crop is not null, rect, panel.PhotoRevision, removed);
    }

    /// <inheritdoc/>
    public async Task<PanelCropPreview> PreviewAsync(Guid wallId, Guid panelId, PanelCropRect rect, CancellationToken ct = default)
    {
        rect.Validate();
        await using var db = await OpenAdminContextAsync(wallId, ct);
        var target = await LoadTargetAsync(db, wallId, panelId, ct);
        var plan = await PlanCropAsync(db, target, rect, ct);
        return plan.Preview;
    }

    /// <inheritdoc/>
    public async Task<PanelCropResult> CropAsync(
        Guid wallId, Guid panelId, PanelCropRect rect, bool confirmRemovals, CancellationToken ct = default)
    {
        rect.Validate();
        await using var db = await OpenAdminContextAsync(wallId, ct);
        var target = await LoadTargetAsync(db, wallId, panelId, ct);
        var plan = await PlanCropAsync(db, target, rect, ct);
        if (plan.Preview.NeedsConfirmation && !confirmRemovals)
        {
            return new PanelCropResult(false, plan.Preview, target.Panel.PhotoRevision, 0, false, 0);
        }

        var source = target.Crop?.OriginalPhoto ?? target.Panel.Photo!;
        var cropped = PanelCropImage.Crop(source, plan.OriginalRect);
        KeepOriginal(db, target, db.CurrentUserId, cropped.Rect);
        target.Panel.Photo = cropped.Photo;
        target.Panel.PhotoContentType = cropped.ContentType;
        target.Panel.PhotoRevision++;

        var historic = await ApplyAsync(db, target, plan.Map, plan.Preview.RemovedHoldIds, CropLabel, ct);
        logger.LogInformation(
            "Panel {PanelId} on wall {WallId} cropped to ({Left:F3},{Top:F3},{Width:F3},{Height:F3}) of its original: {Kept} holds re-mapped, {Removed} removed, {Historic} boulder(s) made historic",
            panelId, wallId, cropped.Rect.Left, cropped.Rect.Top, cropped.Rect.Width, cropped.Rect.Height,
            plan.Preview.KeptHoldCount, plan.Preview.RemovedHoldIds.Count, historic);
        return new PanelCropResult(true, plan.Preview, target.Panel.PhotoRevision, historic, false, 0);
    }

    /// <summary>A context for a wall-admin mutation: not a kiosk, signed in, admin of the wall.</summary>
    private async Task<BlocwerkDbContext> OpenAdminContextAsync(Guid wallId, CancellationToken ct)
    {
        KioskGuard.EnsureNotKiosk(kioskContext, "Cropping a panel photo");
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        try
        {
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates the kept original on a first crop; on a re-crop only the rectangle moves.</summary>
    private static void KeepOriginal(BlocwerkDbContext db, CropTarget target, Guid? userId, PanelCropRect originalRect)
    {
        var crop = target.Crop;
        if (crop is null)
        {
            crop = new WallPanelCrop
            {
                WallPanelId = target.Panel.Id,
                OriginalPhoto = target.Panel.Photo!,
                OriginalPhotoContentType = target.Panel.PhotoContentType,
            };
            db.WallPanelCrops.Add(crop);
        }

        (crop.Left, crop.Top, crop.Width, crop.Height) = (originalRect.Left, originalRect.Top, originalRect.Width, originalRect.Height);
        crop.CroppedAt = DateTimeOffset.UtcNow;
        crop.CroppedByUserId = userId;
    }

    /// <summary>
    /// The live panel to crop, tracked, with its kept original. Refused while a wall update is in flight (its staged
    /// photos and holds were matched against the current frame) and for any panel row that is not the live one at its cell.
    /// </summary>
    private static async Task<CropTarget> LoadTargetAsync(BlocwerkDbContext db, Guid wallId, Guid panelId, CancellationToken ct)
    {
        var panel = await db.WallPanels.FirstOrDefaultAsync(p => p.Id == panelId && p.WallId == wallId, ct)
            ?? throw new InvalidOperationException("Panel not found.");
        if (panel.Photo is null || panel.StagedPhoto is not null)
        {
            throw new InvalidOperationException("Only a live panel photo can be cropped.");
        }

        var superseded = await db.WallPanels.AnyAsync(
            p => p.WallId == wallId && p.Col == panel.Col && p.Row == panel.Row && p.Generation > panel.Generation, ct);
        var updating = await db.WallUpdateSessions.AnyAsync(s => s.WallId == wallId && s.Status == WallUpdateSessionStatus.Open, ct);
        if (superseded || updating)
        {
            throw new InvalidOperationException(superseded
                ? "This panel photo has been replaced by a newer one; crop the current photo instead."
                : "A wall update is in progress; finish or discard it before cropping a panel photo.");
        }

        var crop = await db.WallPanelCrops.FirstOrDefaultAsync(c => c.WallPanelId == panelId, ct);
        return new CropTarget(panel, crop);
    }
}
