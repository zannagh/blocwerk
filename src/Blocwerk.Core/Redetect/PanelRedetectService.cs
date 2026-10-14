// <copyright file="PanelRedetectService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds.Colors;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// <see cref="IPanelRedetectService"/>. Detection and outlining run on holds that are never stored; what they mean for
/// the stored holds is decided by <see cref="RedetectPlanner"/>, the same way when planning and when applying. The
/// writing is in the Apply part.
/// </summary>
/// <remarks>
/// It replaces a re-detect that deleted every automatic hold no boulder used and stored the detections as new holds,
/// at once and without asking. Only automatic holds that nothing depends on may still go, and only after the confirm
/// said how many.
/// <para>
/// A "clean panel artifacts" tool used to live next to it: one toolbar button that deleted every unreferenced
/// auto-detected hold on the panel, with no preview and no confirmation. On 2026-09-20 an admin hit it by accident
/// on a live wall and it destroyed 181 real holds (restored from the change journal). It was removed end to end. Any
/// tool that removes detections must show what it would delete and take an explicit confirmation.
/// </para>
/// </remarks>
public sealed partial class PanelRedetectService : IPanelRedetectService
{
    /// <summary>The journal batch label of the shapes, new holds and removals of one re-detect.</summary>
    public const string BatchLabel = "panel-redetect";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IHoldDetectionService holdDetectionService;
    private readonly HoldSamenessOptions options;
    private readonly ILogger<PanelRedetectService> logger;
    private readonly IHoldEnrichmentService? holdEnrichment;
    private readonly IHoldDuplicateService? duplicates;
    private readonly IChangeJournal? journal;
    private readonly IKioskContext? kioskContext;
    private readonly IHoldStackService? stacks;
    private readonly IHoldRefinementQueue? refinement;
    private readonly BlocwerkSettings? settings;
    private readonly IMarkerDetectionService? markers;

    /// <summary>Initializes a new instance of the <see cref="PanelRedetectService"/> class.</summary>
    /// <param name="dbContextFactory">Context factory.</param>
    /// <param name="currentUserService">The acting user.</param>
    /// <param name="holdDetectionService">The hold detector.</param>
    /// <param name="options">The thresholds for "the same hold".</param>
    /// <param name="logger">Logger.</param>
    /// <param name="holdEnrichment">Outlines the detections; without it they stay circles.</param>
    /// <param name="duplicates">Merges two holds; without it nothing is merged.</param>
    /// <param name="journal">Names the journal batch; optional as on the other hold services.</param>
    /// <param name="kioskContext">The kiosk context, when the host has one.</param>
    /// <param name="stacks">Records that a hold sits on another; without it accepted stacks are not written.</param>
    /// <param name="refinement">Refines the 3D shapes of the holds whose outline changed, in the background.</param>
    /// <param name="settings">The installation's settings: with them, re-detect only runs where it is switched on.</param>
    /// <param name="markers">Finds the printed markers on the photo that are not stored for it; without it only the stored ones are avoided.</param>
    public PanelRedetectService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IHoldDetectionService holdDetectionService,
        HoldSamenessOptions options,
        ILogger<PanelRedetectService> logger,
        IHoldEnrichmentService? holdEnrichment = null,
        IHoldDuplicateService? duplicates = null,
        IChangeJournal? journal = null,
        IKioskContext? kioskContext = null,
        IHoldStackService? stacks = null,
        IHoldRefinementQueue? refinement = null,
        BlocwerkSettings? settings = null,
        IMarkerDetectionService? markers = null)
    {
        this.markers = markers;
        this.settings = settings;
        this.stacks = stacks;
        this.refinement = refinement;
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.holdDetectionService = holdDetectionService;
        this.options = options;
        this.logger = logger;
        this.holdEnrichment = holdEnrichment;
        this.duplicates = duplicates;
        this.journal = journal;
        this.kioskContext = kioskContext;
    }

    /// <inheritdoc/>
    public async Task<PanelRedetectPlan> PlanAsync(Guid wallId, Guid panelId, CancellationToken ct = default)
    {
        EnsureSwitchedOn();
        var (db, userId) = await OpenForEditorAsync(wallId, ct);
        await using (db)
        {
            var (wall, panel) = await LoadPanelAsync(db, wallId, panelId, ct);
            if (panel?.Photo is null)
            {
                return new PanelRedetectPlan(wallId, panelId, [], PanelRedetectSummary.Nothing);
            }

            var detections = await DetectAsync(db, wall, panel, ct);
            var input = await LoadInputAsync(db, panel, detections, userId, ct);
            var decisions = RedetectPlanner.Plan(input, options);
            var (proposals, size) = await ProposalsAsync(db, panel, input, decisions, ct);
            var involved = proposals.SelectMany(p => p.OtherHoldIds.Concat(p.HoldId is { } id ? [id] : [])).ToHashSet();
            return new PanelRedetectPlan(wallId, panelId, detections, Summarize(input, decisions))
            {
                Proposals = proposals,
                Holds = input.Existing.Where(e => involved.Contains(e.Hold.Id)).ToDictionary(e => e.Hold.Id, e => e.Hold.Clone()),
                TurnedDown = decisions.TurnedDown,
                Untraced = decisions.Untraced,
                PhotoAspect = size is { Height: > 0 } s ? (double)s.Width / s.Height : 1,
            };
        }
    }

    /// <summary>The proposals for the decisions, with the photo's size when it is known.</summary>
    private async Task<(List<RedetectProposal> Proposals, (int Width, int Height)? Size)> ProposalsAsync(
        BlocwerkDbContext db, WallPanel panel, RedetectInput input, RedetectDecisions decisions, CancellationToken ct)
    {
        var size = (await LookOverFrameSource.SizesAsync(db, [panel.Id], ct)).TryGetValue(panel.Id, out var known) ? known : ((int Width, int Height)?)null;
        double aspect = size is { Height: > 0 } s ? (double)s.Width / s.Height : 1;
        return (RedetectProposals.Build(input, decisions, Stacks(input, size), options, aspect), size);
    }

    /// <summary>Re-detect runs only where the installation switched it on; a host without settings (a tool, a test) decides for itself.</summary>
    private void EnsureSwitchedOn()
    {
        if (settings is { HoldDetection.RedetectEnabled: false })
        {
            throw new UserFacingException("Re-detect is switched off on this installation.");
        }
    }

    /// <summary>
    /// The stored holds of the panel that sit on another stored hold and are not recorded as such. Stacking is a wall
    /// admin's decision, like a merge: for anybody else nothing is proposed.
    /// </summary>
    private static List<HoldStackPlan> Stacks(RedetectInput input, (int Width, int Height)? size)
    {
        if (!input.CanMerge)
        {
            return [];
        }

        // A bolt hole or a pocket that was recorded as a hold lies inside its hold too: that is no hold on a hold.
        var radii = input.Existing.Select(e => e.Hold.Radius).Order().ToList();
        var median = radii.Count == 0 ? 0 : radii[radii.Count / 2];
        return [.. HoldStacking.Plan([.. input.Existing.Select(e => e.Hold).Where(RedetectPlanner.IsMatchable)], null, size)
            .Where(s => !RedetectRules.IsPart(s.Upper, input, median))];
    }

    private static PanelRedetectSummary Summarize(RedetectInput input, RedetectDecisions decisions)
    {
        var byId = input.Existing.ToDictionary(e => e.Hold.Id);
        var merges = decisions.Merges.Select(m =>
        {
            var (kept, removed) = (byId[m.KeptId], byId[m.RemovedId]);
            return new RedetectMergeInfo(
                m.KeptId,
                m.RemovedId,
                string.IsNullOrWhiteSpace(kept.Hold.Name) ? removed.Hold.Name : kept.Hold.Name,
                string.IsNullOrWhiteSpace(kept.Hold.Color) ? removed.Hold.Color : kept.Hold.Color,
                removed.Boulders.Count);
        }).ToList();
        return new PanelRedetectSummary(
            input.Detections.Count,
            decisions.Shapes.Count(s => s.Change == RedetectShapeChange.Replace),
            decisions.Shapes.Count(s => s.Change == RedetectShapeChange.FillOutline),
            decisions.NewDetections.Count,
            merges,
            decisions.ForReview.Count,
            decisions.Removed.Count,
            decisions.HandShapesKept,
            decisions.SkippedDetections);
    }

    /// <summary>The colour the estimator reads for each detection on the photo; null where it cannot tell, or without a readable photo.</summary>
    private static List<string?> ReadColors(byte[] photo, List<Hold> holds)
    {
        using var pixels = HoldPhotoPixels.TryOpen(photo);
        return [.. holds.Select(h => pixels is null ? null : HoldColorSampler.Estimate(h, pixels.Width, pixels.Height, pixels.At).Color)];
    }

    /// <summary>The wall and the panel, tracked; the panel is null when it is not one of the wall's live panels.</summary>
    private static async Task<(Wall Wall, WallPanel? Panel)> LoadPanelAsync(BlocwerkDbContext db, Guid wallId, Guid panelId, CancellationToken ct)
    {
        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId, ct)
            ?? throw new InvalidOperationException("Wall not found");
        var panel = await db.WallPanels.FirstOrDefaultAsync(p => p.Id == panelId && p.WallId == wallId, ct)
            ?? throw new InvalidOperationException("Panel not found");
        var live = panel.Photo is not null && (await LiveWallHolds.LoadPanelIdsAsync(db, wallId, ct)).Contains(panelId);
        return (wall, live ? panel : null);
    }

    /// <summary>
    /// Detects and outlines on holds that are not stored. The outliner plans marker rows on the context it is given;
    /// they belong to the ingest of the photo, not to this dry run, so the context is cleared again.
    /// </summary>
    private async Task<List<RedetectDetection>> DetectAsync(BlocwerkDbContext db, Wall wall, WallPanel panel, CancellationToken ct)
    {
        var photo = panel.Photo!;

        // The detector works for seconds on the calling thread; from the editor that thread is the page's own.
        var detected = await Task.Run(() => holdDetectionService.DetectHoldsAsync(photo), ct);
        var fresh = detected.Select(d => new Hold
        {
            WallId = wall.Id,
            WallPanelId = panel.Id,
            X = d.X,
            Y = d.Y,
            Radius = d.Radius,
            DetectedBox = HoldDetectedBox.Of(d),
            Color = d.Color,
            Confidence = d.Confidence,
            IsAutoDetected = true,
            NeedsReview = true,
            Generation = panel.Generation,
        }).ToList();
        var enrichment = await holdEnrichment.EnrichSafelyAsync(
            db, new HoldEnrichmentRequest(photo, wall, fresh, panel.Id, panel.Generation), logger);
        db.ChangeTracker.Clear();
        var kept = fresh.Except(enrichment.DroppedMarkerHolds).ToList();
        var colors = await Task.Run(() => ReadColors(photo, kept), ct);
        return [.. kept.Select((h, i) => new RedetectDetection(h, colors[i] ?? h.Color, colors[i] is not null))];
    }

    /// <summary>The panel's stored holds (tracked) with their boulders and links, and what a person already ruled out.</summary>
    private async Task<RedetectInput> LoadInputAsync(
        BlocwerkDbContext db, WallPanel panel, IReadOnlyList<RedetectDetection> detections, Guid userId, CancellationToken ct)
    {
        var holds = await db.Holds.Where(h => h.WallPanelId == panel.Id && h.Generation == panel.Generation).ToListAsync(ct);
        var ids = holds.Select(h => h.Id).ToList();
        var uses = await db.BoulderHolds.AsNoTracking()
            .Where(bh => ids.Contains(bh.HoldId))
            .Select(bh => new { bh.HoldId, bh.BoulderId, Active = !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric })
            .ToListAsync(ct);
        var linked = (await db.HoldLinks.AsNoTracking()
                .Where(l => l.WallId == panel.WallId && (ids.Contains(l.HoldAId) || ids.Contains(l.HoldBId)))
                .Select(l => new { l.HoldAId, l.HoldBId })
                .ToListAsync(ct))
            .SelectMany(l => new[] { l.HoldAId, l.HoldBId })
            .ToHashSet();
        var dismissed = (await db.HoldDuplicateDismissals.AsNoTracking().Where(d => d.WallId == panel.WallId)
                .Select(d => new { d.HoldAId, d.HoldBId }).ToListAsync(ct))
            .Select(d => (d.HoldAId, d.HoldBId)).ToHashSet();
        var byHold = uses.ToLookup(u => u.HoldId);
        var existing = holds.Select(h => new RedetectExisting(
            h,
            byHold[h.Id].Select(u => u.BoulderId).ToHashSet(),
            byHold[h.Id].Count(u => u.Active),
            linked.Contains(h.Id))).ToList();
        return new RedetectInput(existing, detections, dismissed, await CanMergeAsync(db, panel.WallId, userId, ct))
        {
            TurnedDown = await RedetectHistory.TurnedDownAsync(db, panel, ct),
            HandEdited = await RedetectHistory.HandEditedAsync(db, panel, ct),
            Markers = panel.Photo is { } photo ? await RedetectMarkers.LoadAsync(db, panel, photo, markers, logger, ct) : [],
        };
    }

    /// <summary>Merging is the duplicate review's operation: a wall admin's, and never a kiosk tablet's.</summary>
    private async Task<bool> CanMergeAsync(BlocwerkDbContext db, Guid wallId, Guid userId, CancellationToken ct) =>
        duplicates is not null
        && KioskGuard.ScopedWallId(kioskContext, db) is null
        && await WallAdminGuard.IsWallAdminAsync(db, wallId, userId, ct);

    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForEditorAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
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
