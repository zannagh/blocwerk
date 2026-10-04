// <copyright file="RefreshCheckCrops.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The pictures of a confirm-screen card, made when the page asks for one (never in advance: a big wall can have many
/// cards) and kept in a small in-memory cache. Everything they show is fixed while the update is open (the old photos,
/// the staged photos, the textures of a model), and a card's id is new whenever the cards are written again, so a
/// cached picture never goes stale. The caller has checked that the user may see the update.
/// </summary>
internal static class RefreshCheckCrops
{
    private const int MaxCached = 256;
    private const double DefaultRadiusMm = 50;

    private static readonly ConcurrentDictionary<(Guid SessionId, Guid CheckId, CheckCropView View), byte[]?> Cache = new();

    /// <summary>The JPEG of one picture of a card of <paramref name="sessionId"/>, or null when there is none.</summary>
    public static async Task<byte[]?> GetAsync(
        BlocwerkDbContext db, ICaptureFileStore files, Guid sessionId, Guid checkId, CheckCropView view, CancellationToken ct)
    {
        var key = (sessionId, checkId, view);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var card = await db.WallUpdateExceptions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == checkId && e.SessionId == sessionId, ct);
        if (card is null)
        {
            return null;
        }

        var bytes = view switch
        {
            CheckCropView.Old => await OldAsync(db, card, ct),
            CheckCropView.New => await NewAsync(db, card, ct),
            _ => await ModelAsync(db, files, card, ct),
        };
        if (Cache.Count >= MaxCached)
        {
            Cache.Clear();
        }

        Cache[key] = bytes;
        return bytes;
    }

    private static async Task<byte[]?> OldAsync(BlocwerkDbContext db, WallUpdateException card, CancellationToken ct)
    {
        if (await HoldAsync(db, card.OldHoldId, ct) is not { } hold)
        {
            return null;
        }

        var photo = hold.WallPanelId is { } panelId
            ? await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync(ct)
            : null;
        photo ??= await db.Walls.AsNoTracking().Where(w => w.Id == hold.WallId).Select(w => w.Photo).FirstOrDefaultAsync(ct);
        return Crop(photo, hold.X, hold.Y, hold.Radius);
    }

    private static async Task<byte[]?> NewAsync(BlocwerkDbContext db, WallUpdateException card, CancellationToken ct)
    {
        if (card is { Kind: UpdateExceptionKind.PossiblyRemoved, PanelId: { } panelId, X: { } x, Y: { } y })
        {
            var old = await HoldAsync(db, card.OldHoldId, ct);
            return Crop(await StagedPhotoAsync(db, panelId, ct), x, y, old?.Radius ?? 0.02);
        }

        if (await HoldAsync(db, card.StagedHoldId, ct) is not { WallPanelId: { } stagedPanel } staged)
        {
            return null;
        }

        return Crop(await StagedPhotoAsync(db, stagedPanel, ct), staged.X, staged.Y, staged.Radius);
    }

    private static async Task<byte[]?> ModelAsync(BlocwerkDbContext db, ICaptureFileStore files, WallUpdateException card, CancellationToken ct)
    {
        var old = await HoldAsync(db, card.OldHoldId, ct);
        var radiusMm = old is { WidthMm: { } w, HeightMm: { } h } ? Math.Max(w, h) / 2 : DefaultRadiusMm;
        var (modelId, facetId, a, b) = (card.GeometryModelId, card.FacetId, card.A, card.B);
        if (facetId is null && card.Kind == UpdateExceptionKind.LowConfidenceMatch && old is { FacetId: { } f, PlaneAMm: { } pa, PlaneBMm: { } pb })
        {
            modelId = (await ActiveModelTextures.FindAsync(db, old.WallId, ct))?.Id;
            (facetId, a, b) = (f, pa, pb);
        }

        if (modelId is null || facetId is null || a is null || b is null)
        {
            return null;
        }

        var texture = await db.WallGeometryTextures.AsNoTracking()
            .FirstOrDefaultAsync(t => t.GeometryModelId == modelId && t.FacetId == facetId, ct);
        var image = texture is null ? null : await files.ReadAsync(texture.StoredPath, ct);
        if (texture is null || image is null)
        {
            return null;
        }

        var frame = TexturePlaneFrame.Of(texture);
        var (px, py) = frame.ToPixel(a.Value, b.Value);
        return frame.IsValid ? RingCrop.Render(image, px, py, radiusMm / frame.MmPerPxA) : null;
    }

    private static Task<Hold?> HoldAsync(BlocwerkDbContext db, Guid? id, CancellationToken ct) =>
        id is { } holdId ? db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct) : Task.FromResult<Hold?>(null);

    private static Task<byte[]?> StagedPhotoAsync(BlocwerkDbContext db, Guid panelId, CancellationToken ct) =>
        db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.StagedPhoto).FirstOrDefaultAsync(ct);

    /// <summary>A crop at a normalised point with a normalised radius (of the long side), or null.</summary>
    private static byte[]? Crop(byte[]? photo, double x, double y, double radius)
    {
        if (photo is null || OverlapSeedLoader.RawSize(photo) is not { } size)
        {
            return null;
        }

        return RingCrop.Render(photo, x * size.Width, y * size.Height, radius * Math.Max(size.Width, size.Height));
    }
}
