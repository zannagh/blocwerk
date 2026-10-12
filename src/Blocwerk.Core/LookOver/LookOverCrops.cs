// <copyright file="LookOverCrops.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The pictures of a look-over finding, like the confirm screen's (<see cref="RefreshCheckCrops"/>) but read after the
/// update went live: the old photo is the photo of the retired panel the old hold is still on, the new photo is the
/// live photo of the finding's panel at the spot stored with it. Made on request, kept in a small cache; a finding's
/// spot and both photos never change. The caller has checked that the user may look over the wall.
/// </summary>
internal static class LookOverCrops
{
    private const int MaxCached = 256;
    private const double DefaultRadiusMm = 50;
    private static readonly ConcurrentDictionary<(Guid FindingId, CheckCropView View), byte[]> Cache = new();

    /// <summary>The JPEG of one picture of a finding of <paramref name="wallId"/>, or null when there is none.</summary>
    public static async Task<byte[]?> GetAsync(
        IDbContextFactory<BlocwerkDbContext> dbFactory, ICaptureFileStore files, Guid wallId, Guid findingId, CheckCropView view, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var finding = await db.WallLookOverFindings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == findingId && f.WallId == wallId, ct);
        if (finding is null)
        {
            return null;
        }

        if (Cache.TryGetValue((findingId, view), out var cached))
        {
            return cached;
        }

        var bytes = view switch
        {
            CheckCropView.Old => await OldAsync(db, finding, ct),
            CheckCropView.New => await NewAsync(db, finding, ct),
            _ => await ModelAsync(db, files, finding, ct),
        };

        // The 3D picture follows the live hold's placement, which a later placement run may move: not cached.
        if (bytes is not null && view != CheckCropView.Model)
        {
            if (Cache.Count >= MaxCached)
            {
                Cache.Clear();
            }

            Cache[(findingId, view)] = bytes;
        }

        return bytes;
    }

    private static async Task<byte[]?> OldAsync(BlocwerkDbContext db, WallLookOverFinding finding, CancellationToken ct)
    {
        if (await HoldAsync(db, finding.OldHoldId, ct) is not { } hold)
        {
            return null;
        }

        var photo = hold.WallPanelId is { } panelId
            ? await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync(ct)
            : null;
        return RefreshCheckCrops.Crop(photo, hold.X, hold.Y, hold.Radius).Bytes;
    }

    private static async Task<byte[]?> NewAsync(BlocwerkDbContext db, WallLookOverFinding finding, CancellationToken ct)
    {
        if (finding.PanelId is not { } panelId)
        {
            return null;
        }

        var photo = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync(ct);
        return RefreshCheckCrops.Crop(photo, finding.X, finding.Y, finding.Radius > 0 ? finding.Radius : 0.02).Bytes;
    }

    /// <summary>The spot stored with the finding (this visit's model), else where the live hold sits on the active model.</summary>
    private static async Task<byte[]?> ModelAsync(BlocwerkDbContext db, ICaptureFileStore files, WallLookOverFinding finding, CancellationToken ct)
    {
        var hold = await HoldAsync(db, finding.HoldId, ct) ?? await HoldAsync(db, finding.OldHoldId, ct);
        var radiusMm = hold is { WidthMm: { } w, HeightMm: { } h } ? Math.Max(w, h) / 2 : DefaultRadiusMm;
        var (modelId, facetId, a, b) = (finding.GeometryModelId, finding.FacetId, finding.A, finding.B);
        if (facetId is null && hold is { FacetId: { } f, PlaneAMm: { } pa, PlaneBMm: { } pb })
        {
            modelId = (await ActiveModelTextures.FindAsync(db, finding.WallId, ct))?.Id;
            (facetId, a, b) = (f, pa, pb);
        }

        return (await RefreshCheckCrops.TextureCropAsync(db, files, modelId, facetId, a, b, radiusMm)).Bytes;
    }

    private static Task<Hold?> HoldAsync(BlocwerkDbContext db, Guid? id, CancellationToken ct) =>
        id is { } holdId ? db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct) : Task.FromResult<Hold?>(null);
}
