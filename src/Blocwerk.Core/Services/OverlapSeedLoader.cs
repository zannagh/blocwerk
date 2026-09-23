using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Blocwerk.Core.Services;

/// <summary>Identifies one panel photo whose marker observations seed a match.</summary>
/// <param name="PanelId">The panel.</param>
/// <param name="Staged">True for the panel's staged photo, false for its live photo.</param>
/// <param name="Image">The photo bytes the matcher receives (used only for the raw frame size).</param>
/// <param name="Holds">The holds in the SAME order as the matcher holds built from them (index = matcher id).</param>
public sealed record OverlapSeedSide(Guid PanelId, bool Staged, byte[] Image, IReadOnlyList<Hold> Holds);

/// <summary>
/// Loads what a glyph wall knows about two panel photos — their marker observations, the active wall
/// model, the holds' plane positions and fingerprints — and turns it into a <see cref="HoldOverlapSeed"/>.
/// Returns null (the matcher then runs exactly as on any other wall) unless the wall has
/// <see cref="Wall.GlyphsEnabled"/> and something useful was found. Never throws: a seed is an
/// optimisation, so any failure is logged and the plain match proceeds.
/// </summary>
public static class OverlapSeedLoader
{
    private const int SupportedGeometryVersion = 1;

    /// <summary>Builds the seed for matching <paramref name="left"/> against <paramref name="right"/>.</summary>
    public static async Task<HoldOverlapSeed?> LoadAsync(
        BlocwerkDbContext db, Wall wall, OverlapSeedSide left, OverlapSeedSide right, ILogger logger)
    {
        if (!wall.GlyphsEnabled)
        {
            return null;
        }

        try
        {
            var document = await LoadDocumentAsync(db, wall.Id);
            var leftPhoto = await LoadPhotoAsync(db, left);
            var rightPhoto = await LoadPhotoAsync(db, right);
            var seedHolds = left.Holds.Select(h => new SeedHold(h.X, h.Y, h.FacetId)).ToList();
            var seed = leftPhoto is null || rightPhoto is null
                ? null
                : OverlapSeedBuilder.Build(leftPhoto, rightPhoto, document, seedHolds);
            var anchors = WallSpaceAnchorProposer.Propose(ToWallSpace(left.Holds), ToWallSpace(right.Holds));

            logger.LogInformation(
                "[Seed] wall {WallId}: markers L={LeftMarkers} R={RightMarkers} model={HasModel} | seed={Source} facet={FacetId} "
                + "markers={SeedMarkers} rmsPx={RmsPx:F1} measured={Measured} priors={Priors} | anchors={Anchors}",
                wall.Id, leftPhoto?.Markers.Count ?? 0, rightPhoto?.Markers.Count ?? 0, document is not null,
                seed?.Source.ToString() ?? "none", seed?.FacetId, seed?.MarkerCount ?? 0, seed?.FitRmsPx ?? double.NaN,
                seed?.MeasuredPairs.Count ?? 0, seed?.PriorPairs.Count ?? 0, anchors.Count);

            if (seed is null && anchors.Count == 0)
            {
                return null;
            }

            return (seed ?? new HoldOverlapSeed()) with { Anchors = anchors };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Marker seed failed on wall {WallId}; matching without it", wall.Id);
            return null;
        }
    }

    /// <summary>Maps holds (by list position) to the anchor rule's view of them.</summary>
    internal static List<WallSpaceHold> ToWallSpace(IReadOnlyList<Hold> holds)
    {
        var list = new List<WallSpaceHold>(holds.Count);
        for (var i = 0; i < holds.Count; i++)
        {
            var h = holds[i];
            double? size = h.WidthMm is > 0 && h.HeightMm is > 0 ? Math.Max(h.WidthMm.Value, h.HeightMm.Value) : null;
            list.Add(new WallSpaceHold(i, h.FacetId, h.PlaneAMm, h.PlaneBMm, size, HoldFingerprint.FromJson(h.FingerprintJson)));
        }

        return list;
    }

    /// <summary>
    /// The RAW (un-oriented) pixel size of an encoded photo — the frame hold X/Y and marker corners are
    /// normalized against — or null when the bytes are not a decodable image.
    /// </summary>
    internal static (int Width, int Height)? RawSize(byte[] image)
    {
        try
        {
            using var data = SKData.CreateCopy(image);
            using var codec = SKCodec.Create(data);
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<SeedPhoto?> LoadPhotoAsync(BlocwerkDbContext db, OverlapSeedSide side)
    {
        var size = RawSize(side.Image);
        if (size is null)
        {
            return null;
        }

        var rows = await db.WallMarkerObservations
            .AsNoTracking()
            .Where(o => o.WallPanelId == side.PanelId && o.FromStagedPhoto == side.Staged)
            .ToListAsync();
        if (rows.Count == 0)
        {
            return null;
        }

        var generation = rows.Max(o => o.PanelGeneration);
        var (w, h) = size.Value;
        var markers = rows
            .Where(o => o.PanelGeneration == generation)
            .Select(o => ToMarker(o, w, h))
            .OfType<DetectedMarker>()
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .ToList();
        return new SeedPhoto(markers, w, h);
    }

    private static DetectedMarker? ToMarker(WallMarkerObservation o, int width, int height)
    {
        double[][]? corners;
        try
        {
            corners = JsonSerializer.Deserialize<double[][]>(o.CornersJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (corners is not { Length: 4 } || corners.Any(c => c.Length < 2))
        {
            return null;
        }

        var norm = corners.Select(c => new MarkerPoint(c[0], c[1])).ToList();
        var px = norm.Select(c => new MarkerPoint(c.X * width, c.Y * height)).ToList();
        return new DetectedMarker
        {
            Id = o.MarkerId, CornersNormalized = norm, CornersPx = px, SidePx = o.SidePx, EdgeRatio = 1, Synthetic = o.Synthetic,
        };
    }

    private static async Task<WallGeometryDocument?> LoadDocumentAsync(BlocwerkDbContext db, Guid wallId)
    {
        var json = await db.WallGeometryModels
            .AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => m.Json)
            .FirstOrDefaultAsync();
        if (json is null)
        {
            return null;
        }

        try
        {
            var document = WallGeometryDocument.Parse(json);
            return document.Version is > 0 and <= SupportedGeometryVersion ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
