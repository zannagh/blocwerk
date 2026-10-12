// <copyright file="LookOverFrameSource.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// Where the "before" and the "now" picture of a finding are cut out of their photos, worked out in ONE place for the
/// server that cuts the pictures and for the list that tells the page where the holds' outlines lie on them: the two
/// must agree to the pixel. "Before" is three radii around the old hold on the old photo; "now" is the same piece of
/// wall on the new photo, by the local scale between the photos (<see cref="LookOverScale"/>), or its own three radii
/// when no scale can be told. Loaded once for a list of findings (the holds, the photos' sizes, the carried holds).
/// </summary>
internal sealed class LookOverFrameSource
{
    private static readonly ConcurrentDictionary<Guid, (int Width, int Height)> PhotoSizes = new();
    private readonly Dictionary<Guid, Hold> holds;
    private readonly Dictionary<(Guid OldPanel, Guid NewPanel), List<((double X, double Y) Old, (double X, double Y) New, Guid OldHoldId)>> carried;

    private LookOverFrameSource(
        Dictionary<Guid, Hold> holds, Dictionary<(Guid OldPanel, Guid NewPanel), List<((double X, double Y) Old, (double X, double Y) New, Guid OldHoldId)>> carried)
    {
        this.holds = holds;
        this.carried = carried;
    }

    /// <summary>Loads what the frames of <paramref name="findings"/> need.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="findings">The findings.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The source.</returns>
    public static async Task<LookOverFrameSource> LoadAsync(BlocwerkDbContext db, Guid wallId, IReadOnlyList<WallLookOverFinding> findings, CancellationToken ct)
    {
        var ids = findings.SelectMany(f => new[] { f.HoldId, f.OtherHoldId, f.OldHoldId }).OfType<Guid>().Distinct().ToList();
        var holds = await db.Holds.AsNoTracking().Where(h => h.WallId == wallId && ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        var panels = holds.Values.Select(h => h.WallPanelId).Concat(findings.Select(f => f.PanelId)).OfType<Guid>().Distinct().ToList();
        await LoadSizesAsync(db, panels, ct);
        var generations = findings.Select(f => f.Generation).Distinct().ToList();
        var pairs = await (
            from link in db.HoldGenerationLinks.AsNoTracking()
            where link.WallId == wallId && generations.Contains(link.ToGeneration)
            join before in db.Holds.AsNoTracking() on link.OldHoldId equals before.Id
            join after in db.Holds.AsNoTracking() on link.NewHoldId equals after.Id
            where before.WallPanelId != null && after.WallPanelId != null
            select new { OldPanel = before.WallPanelId!.Value, NewPanel = after.WallPanelId!.Value, OldId = before.Id, OldX = before.X, OldY = before.Y, NewX = after.X, NewY = after.Y })
            .ToListAsync(ct);
        var carried = pairs
            .Where(p => PhotoSizes.ContainsKey(p.OldPanel) && PhotoSizes.ContainsKey(p.NewPanel))
            .GroupBy(p => (p.OldPanel, p.NewPanel))
            .ToDictionary(g => g.Key, g => g.Select(p => (Px(p.OldX, p.OldY, PhotoSizes[p.OldPanel]), Px(p.NewX, p.NewY, PhotoSizes[p.NewPanel]), p.OldId)).ToList());
        return new LookOverFrameSource(holds, carried);
    }

    /// <summary>A hold of the findings as it is now, or null when it is gone.</summary>
    /// <param name="id">The hold's id.</param>
    /// <returns>The hold.</returns>
    public Hold? Hold(Guid? id) => id is { } holdId ? holds.GetValueOrDefault(holdId) : null;

    /// <summary>Where the "before" picture is cut: around the old hold on its (retired) panel's photo.</summary>
    /// <param name="finding">The finding.</param>
    /// <returns>The frame, or null without an old hold or a readable photo.</returns>
    public CropFrame? Before(WallLookOverFinding finding)
    {
        if (Hold(finding.OldHoldId) is not { WallPanelId: { } panel } old || !PhotoSizes.TryGetValue(panel, out var size))
        {
            return null;
        }

        // An old and a new hold on one spot are compared: the larger of the two sets the scale of both pictures.
        var radius = finding.Kind == LookOverFindingKind.SameSpot ? Math.Max(old.Radius, finding.Radius) : old.Radius;
        var ring = radius * Math.Max(size.Width, size.Height);
        return new CropFrame(old.X * size.Width, old.Y * size.Height, RingCrop.HalfFor(ring), ring, size.Width, size.Height);
    }

    /// <summary>Where the "now" picture is cut: the piece of wall "before" shows, found on the new photo.</summary>
    /// <param name="finding">The finding.</param>
    /// <returns>The frame, or null without a readable photo.</returns>
    public CropFrame? Now(WallLookOverFinding finding)
    {
        if (finding.PanelId is not { } panel || !PhotoSizes.TryGetValue(panel, out var size))
        {
            return null;
        }

        var centre = (X: finding.X * size.Width, Y: finding.Y * size.Height);
        if (Before(finding) is { } before && Hold(finding.OldHoldId) is { WallPanelId: { } oldPanel } old
            && carried.TryGetValue((oldPanel, panel), out var known)
            && LookOverScale.Local((before.CentreX, before.CentreY), centre, known.Where(k => k.OldHoldId != old.Id).Select(k => (k.Old, k.New))) is { } scale)
        {
            return new CropFrame(centre.X, centre.Y, before.HalfPx * scale, before.RingPx * scale, size.Width, size.Height);
        }

        var ring = (finding.Radius > 0 ? finding.Radius : 0.02) * Math.Max(size.Width, size.Height);
        return new CropFrame(centre.X, centre.Y, RingCrop.HalfFor(ring), ring, size.Width, size.Height);
    }

    /// <summary>The pixel sizes of the panels' photos (those that can be read).</summary>
    /// <param name="db">The context.</param>
    /// <param name="panelIds">The panels.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Panel to size.</returns>
    public static async Task<IReadOnlyDictionary<Guid, (int Width, int Height)>> SizesAsync(BlocwerkDbContext db, List<Guid> panelIds, CancellationToken ct)
    {
        await LoadSizesAsync(db, panelIds, ct);
        return panelIds.Where(PhotoSizes.ContainsKey).ToDictionary(id => id, id => PhotoSizes[id]);
    }

    private static (double X, double Y) Px(double x, double y, (int Width, int Height) size) => (x * size.Width, y * size.Height);

    /// <summary>A panel row's photo never changes once it is live, so its size is read from the photo once per process.</summary>
    private static async Task LoadSizesAsync(BlocwerkDbContext db, List<Guid> panelIds, CancellationToken ct)
    {
        foreach (var id in panelIds.Where(id => !PhotoSizes.ContainsKey(id)))
        {
            var photo = await db.WallPanels.AsNoTracking().Where(p => p.Id == id).Select(p => p.Photo).FirstOrDefaultAsync(ct);
            if (photo is not null && OverlapSeedLoader.RawSize(photo) is { } size)
            {
                PhotoSizes[id] = size;
            }
        }
    }
}
