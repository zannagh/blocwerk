// <copyright file="HybridZoomService.Panel.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>The panel photo the colour match compares against: decoded once, small and upright, and cached per photo stamp.</summary>
public sealed partial class HybridZoomService
{
    /// <summary>Long side the panel photo is decoded at for the colour match (statistics only).</summary>
    private const int PanelMatchEdge = 1536;

    /// <summary>
    /// A lease on the panel photo, upright and at most <see cref="PanelMatchEdge"/> px; null when it cannot be read.
    /// Dispose it after the render: the cache frees dropped photos only once no render holds them.
    /// </summary>
    private async Task<PanelImageLease?> PanelImageAsync(BlocwerkDbContext db, ZoomPanel panel, CancellationToken ct)
    {
        var key = (panel.Id, panel.Generation, panel.Revision);
        if (cache.AcquirePanel(key) is { } cached)
        {
            return cached;
        }

        var bytes = await db.WallPanels.AsNoTracking().Where(p => p.Id == panel.Id).Select(p => p.Photo).FirstOrDefaultAsync(ct);
        var image = bytes is null ? null : await Task.Run(() => Decode(bytes), ct);
        return image is null ? null : cache.PutPanel(key, image);
    }

    private static SKImage? Decode(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            return null;
        }

        var size = codec.GetScaledDimensions(Math.Min(1f, PanelMatchEdge / (float)Math.Max(codec.Info.Width, codec.Info.Height)));
        using var raw = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(raw.Info, raw.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        // Holds are normalised to the photo as shown, i.e. after its EXIF rotation.
        using var upright = ImageRendition.Reorient(raw, codec.EncodedOrigin);
        return SKImage.FromBitmap(upright ?? raw);
    }
}
