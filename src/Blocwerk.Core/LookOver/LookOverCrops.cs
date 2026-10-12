// <copyright file="LookOverCrops.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

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

        // The 3D picture of a spot stored with the finding never changes either. Without one it follows the live hold's
        // placement, which a later placement run may move: that one is made again each time.
        if (bytes is not null && (view != CheckCropView.Model || finding.FacetId is not null))
        {
            if (Cache.Count >= MaxCached)
            {
                Cache.Clear();
            }

            Cache[(findingId, view)] = bytes;
        }

        return bytes;
    }

    /// <summary>
    /// A texture picture with nothing on it: the edge of the wall or a part the 3D model never painted, which comes out
    /// as one flat black tile. Only a picture that is almost entirely near-black AND almost uniform counts, so a
    /// matte-black hold on a dark wall (dark, but with texture and edges) is still shown. Sampled on a coarse grid; an
    /// image that does not decode counts as empty.
    /// </summary>
    internal static bool MostlyEmpty(byte[] jpeg)
    {
        const int Step = 8;
        const int Dark = 24;
        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data);
        using var bitmap = codec is null ? null : SKBitmap.Decode(codec);
        if (bitmap is null)
        {
            return true;
        }

        var (dark, all, sum, squares) = (0, 0, 0.0, 0.0);
        for (var y = Step / 2; y < bitmap.Height; y += Step)
        {
            for (var x = Step / 2; x < bitmap.Width; x += Step)
            {
                var pixel = bitmap.GetPixel(x, y);
                var light = (pixel.Red + pixel.Green + pixel.Blue) / 3.0;
                all++;
                dark += light < Dark ? 1 : 0;
                sum += light;
                squares += light * light;
            }
        }

        if (all == 0)
        {
            return true;
        }

        var mean = sum / all;
        var deviation = Math.Sqrt(Math.Max(0, (squares / all) - (mean * mean)));
        return dark > all * 0.85 && deviation < 10;
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

        // An old and a new hold on one spot are compared: both pictures at the finding's one scale, not each hold's own.
        var radius = finding.Kind == LookOverFindingKind.SameSpot ? Math.Max(hold.Radius, finding.Radius) : hold.Radius;
        return RefreshCheckCrops.Crop(photo, hold.X, hold.Y, radius).Bytes;
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

        var crop = (await RefreshCheckCrops.TextureCropAsync(db, files, modelId, facetId, a, b, radiusMm)).Bytes;
        return crop is not null && MostlyEmpty(crop) ? null : crop;
    }

    private static Task<Hold?> HoldAsync(BlocwerkDbContext db, Guid? id, CancellationToken ct) =>
        id is { } holdId ? db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct) : Task.FromResult<Hold?>(null);
}
