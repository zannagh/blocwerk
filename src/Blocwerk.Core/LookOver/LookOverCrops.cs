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
    private static readonly ConcurrentDictionary<(Guid FindingId, CheckCropView View, bool Plain), byte[]> Cache = new();
    private static readonly ConcurrentDictionary<Guid, bool> NoModelPicture = new();

    /// <summary>
    /// The finding's 3D picture was asked for and there is none worth showing (no texture there, or an empty one): the
    /// list then stops offering it, so the page does not ask again. Remembered for the life of the process only.
    /// </summary>
    public static bool KnownToHaveNoModelPicture(Guid findingId) => NoModelPicture.ContainsKey(findingId);

    /// <summary>
    /// The JPEG of one picture of a finding of <paramref name="wallId"/>, or null when there is none.
    /// <paramref name="plain"/> leaves the ring off: the page draws the holds' outlines on the two photos instead, and shows the 3D
    /// texture bare (a hold's outline is not known on it, and a ring of another size than the hold misleads).
    /// </summary>
    public static async Task<byte[]?> GetAsync(
        IDbContextFactory<BlocwerkDbContext> dbFactory, ICaptureFileStore files, Guid wallId, Guid findingId, CheckCropView view, bool plain, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var finding = await db.WallLookOverFindings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == findingId && f.WallId == wallId, ct);
        if (finding is null)
        {
            return null;
        }

        if (Cache.TryGetValue((findingId, view, plain), out var cached))
        {
            return cached;
        }

        var bytes = view switch
        {
            CheckCropView.Old => await PhotoAsync(db, finding, before: true, plain, ct),
            CheckCropView.New => await PhotoAsync(db, finding, before: false, plain, ct),
            _ => await ModelAsync(db, files, finding, plain, ct),
        };

        // The 3D picture of a spot stored with the finding never changes either. Without one it follows the live hold's
        // placement, which a later placement run may move: that one is made again each time.
        if (bytes is not null && (view != CheckCropView.Model || finding.FacetId is not null))
        {
            if (Cache.Count >= MaxCached)
            {
                Cache.Clear();
            }

            Cache[(findingId, view, plain)] = bytes;
        }
        else if (bytes is null && view == CheckCropView.Model)
        {
            if (NoModelPicture.Count >= MaxCached * 16)
            {
                NoModelPicture.Clear();
            }

            NoModelPicture[findingId] = true;
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

    /// <summary>
    /// "Before": the old hold on its retired panel's photo. "Now": the same piece of wall on the finding's live photo.
    /// Both are cut where <see cref="LookOverFrameSource"/> says, which is also where the page lays the outlines.
    /// </summary>
    private static async Task<byte[]?> PhotoAsync(BlocwerkDbContext db, WallLookOverFinding finding, bool before, bool plain, CancellationToken ct)
    {
        var frames = await LookOverFrameSource.LoadAsync(db, finding.WallId, [finding], ct);
        var panelId = before ? frames.Hold(finding.OldHoldId)?.WallPanelId : finding.PanelId;
        if (panelId is null || (before ? frames.Before(finding) : frames.Now(finding)) is not { } frame)
        {
            return null;
        }

        var photo = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync(ct);
        return photo is null ? null : RingCrop.Render(photo, frame.CentreX, frame.CentreY, frame.RingPx, frame.HalfPx, ring: !plain);
    }

    /// <summary>The spot stored with the finding (this visit's model), else where the live hold sits on the active model.</summary>
    private static async Task<byte[]?> ModelAsync(BlocwerkDbContext db, ICaptureFileStore files, WallLookOverFinding finding, bool plain, CancellationToken ct)
    {
        var hold = await HoldAsync(db, finding.HoldId, ct) ?? await HoldAsync(db, finding.OldHoldId, ct);
        var radiusMm = hold is { WidthMm: { } w, HeightMm: { } h } ? Math.Max(w, h) / 2 : DefaultRadiusMm;
        var (modelId, facetId, a, b) = (finding.GeometryModelId, finding.FacetId, finding.A, finding.B);
        if (facetId is null && hold is { FacetId: { } f, PlaneAMm: { } pa, PlaneBMm: { } pb })
        {
            modelId = (await ActiveModelTextures.FindAsync(db, finding.WallId, ct))?.Id;
            (facetId, a, b) = (f, pa, pb);
        }

        var crop = (await RefreshCheckCrops.TextureCropAsync(db, files, modelId, facetId, a, b, radiusMm, ring: !plain)).Bytes;
        return crop is not null && MostlyEmpty(crop) ? null : crop;
    }

    private static Task<Hold?> HoldAsync(BlocwerkDbContext db, Guid? id, CancellationToken ct) =>
        id is { } holdId ? db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct) : Task.FromResult<Hold?>(null);
}
