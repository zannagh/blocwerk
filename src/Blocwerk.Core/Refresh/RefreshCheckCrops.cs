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
    private static readonly ConcurrentDictionary<CropKey, byte[]?> Cache = new();
    private static readonly ConcurrentDictionary<CropKey, Task<byte[]?>> InFlight = new();

    private readonly record struct CropKey(Guid SessionId, Guid CheckId, CheckCropView View);

    /// <summary>
    /// The JPEG of one picture of a card of <paramref name="sessionId"/>, or null when there is none. A picture there is
    /// none of by design (no old photo, no texture spot) is remembered; a failure (an exception, a photo or texture that
    /// does not read) is not, so the next request tries again. Concurrent requests for one picture share one decode.
    /// </summary>
    public static async Task<byte[]?> GetAsync(
        IDbContextFactory<BlocwerkDbContext> dbFactory, ICaptureFileStore files, Guid sessionId, Guid checkId, CheckCropView view, CancellationToken ct)
    {
        var key = new CropKey(sessionId, checkId, view);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // The shared work belongs to no caller: it opens its own context and is not cancelled by one caller leaving.
        var work = InFlight.GetOrAdd(key, k => Task.Run(() => MakeAsync(dbFactory, files, k)));
        return await work.WaitAsync(ct);
    }

    private static async Task<byte[]?> MakeAsync(IDbContextFactory<BlocwerkDbContext> dbFactory, ICaptureFileStore files, CropKey key)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var card = await db.WallUpdateExceptions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == key.CheckId && e.SessionId == key.SessionId);
            if (card is null)
            {
                return null;
            }

            var (bytes, remember) = key.View switch
            {
                CheckCropView.Old => await OldAsync(db, card),
                CheckCropView.New => await NewAsync(db, card),
                _ => await ModelAsync(db, files, card),
            };
            if (remember || bytes is not null)
            {
                if (Cache.Count >= MaxCached)
                {
                    Cache.Clear();
                }

                Cache[key] = bytes;
            }

            return bytes;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }

    private static async Task<(byte[]? Bytes, bool Remember)> OldAsync(BlocwerkDbContext db, WallUpdateException card)
    {
        if (await HoldAsync(db, card.OldHoldId) is not { } hold)
        {
            return (null, true);
        }

        var photo = hold.WallPanelId is { } panelId
            ? await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync()
            : null;
        photo ??= await db.Walls.AsNoTracking().Where(w => w.Id == hold.WallId).Select(w => w.Photo).FirstOrDefaultAsync();
        return Crop(photo, hold.X, hold.Y, hold.Radius);
    }

    private static async Task<(byte[]? Bytes, bool Remember)> NewAsync(BlocwerkDbContext db, WallUpdateException card)
    {
        if (card is { Kind: UpdateExceptionKind.PossiblyRemoved, PanelId: { } panelId, X: { } x, Y: { } y })
        {
            var old = await HoldAsync(db, card.OldHoldId);
            return Crop(await StagedPhotoAsync(db, panelId), x, y, old?.Radius ?? 0.02);
        }

        if (await HoldAsync(db, card.StagedHoldId) is not { WallPanelId: { } stagedPanel } staged)
        {
            return (null, true);
        }

        return Crop(await StagedPhotoAsync(db, stagedPanel), staged.X, staged.Y, staged.Radius);
    }

    private static async Task<(byte[]? Bytes, bool Remember)> ModelAsync(BlocwerkDbContext db, ICaptureFileStore files, WallUpdateException card)
    {
        var old = await HoldAsync(db, card.OldHoldId);
        var radiusMm = old is { WidthMm: { } w, HeightMm: { } h } ? Math.Max(w, h) / 2 : DefaultRadiusMm;
        var (modelId, facetId, a, b) = (card.GeometryModelId, card.FacetId, card.A, card.B);
        if (facetId is null && card.Kind == UpdateExceptionKind.LowConfidenceMatch && old is { FacetId: { } f, PlaneAMm: { } pa, PlaneBMm: { } pb })
        {
            modelId = (await ActiveModelTextures.FindAsync(db, old.WallId, CancellationToken.None))?.Id;
            (facetId, a, b) = (f, pa, pb);
        }

        if (modelId is null || facetId is null || a is null || b is null)
        {
            return (null, true);
        }

        var texture = await db.WallGeometryTextures.AsNoTracking().FirstOrDefaultAsync(t => t.GeometryModelId == modelId && t.FacetId == facetId);
        if (texture is null)
        {
            return (null, true);
        }

        // A texture row whose file does not read is a failure that may pass, not "no picture".
        var image = await files.ReadAsync(texture.StoredPath, CancellationToken.None);
        var frame = TexturePlaneFrame.Of(texture);
        if (image is null || !frame.IsValid)
        {
            return (null, false);
        }

        var (px, py) = frame.ToPixel(a.Value, b.Value);
        var crop = RingCrop.Render(image, px, py, radiusMm / frame.MmPerPxA);
        return (crop, false);
    }

    private static Task<Hold?> HoldAsync(BlocwerkDbContext db, Guid? id) =>
        id is { } holdId ? db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId) : Task.FromResult<Hold?>(null);

    private static Task<byte[]?> StagedPhotoAsync(BlocwerkDbContext db, Guid panelId) =>
        db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.StagedPhoto).FirstOrDefaultAsync();

    /// <summary>A crop at a normalised point with a normalised radius (of the long side). No photo is "none by design"; one that does not decode is not.</summary>
    private static (byte[]? Bytes, bool Remember) Crop(byte[]? photo, double x, double y, double radius)
    {
        if (photo is null)
        {
            return (null, true);
        }

        if (OverlapSeedLoader.RawSize(photo) is not { } size)
        {
            return (null, false);
        }

        var crop = RingCrop.Render(photo, x * size.Width, y * size.Height, radius * Math.Max(size.Width, size.Height));
        return (crop, false);
    }
}
