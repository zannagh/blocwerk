// <copyright file="NewHoldEvidence3D.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Services;

/// <summary>What the 3D model says about one unpaired staged detection.</summary>
public enum Evidence3DVerdict
{
    /// <summary>Nothing: the photo did not register there, or the spot holds nothing the model knows.</summary>
    None = 0,

    /// <summary>An existing hold is placed at that spot on the model: the matcher missed the pairing.</summary>
    KnownHold = 1,

    /// <summary>The model's photos see a hold there that is not in the app yet (a pending proposal of this visit's model).</summary>
    SeenIn3D = 2,

    /// <summary>The spot lies well off every facet the photo registered to (floor mats, ceiling, a neighbouring wall).</summary>
    OffWall = 3,
}

/// <summary>A point on a facet of the model with a catch radius (mm).</summary>
public sealed record FacetSpot(string FacetId, double A, double B, double RadiusMm);

/// <summary>One staged panel photo registered onto the active model, and what the model knows.</summary>
/// <param name="Registrations">The photo's facet registrations (only accepted ones are used).</param>
/// <param name="KnownHolds">The old holds' placements on the model.</param>
/// <param name="SeenIn3D">Holds the model's photos see that are not in the app (empty when the model is older than this visit).</param>
/// <param name="Facets">Every facet of the model (3D frame and extent); without them nothing is judged off the wall.</param>
/// <param name="Width">Photo width, px.</param>
/// <param name="Height">Photo height, px.</param>
/// <param name="FocalPx">The photo's focal length from EXIF, px, or null.</param>
public sealed record Panel3DEvidence(
    IReadOnlyList<FacetRegistration> Registrations,
    IReadOnlyList<FacetSpot> KnownHolds,
    IReadOnlyList<FacetSpot> SeenIn3D,
    IReadOnlyDictionary<string, ModelFacet>? Facets = null,
    int Width = 0,
    int Height = 0,
    double? FocalPx = null)
{
    public bool IsUsable => Registrations.Any(r => r.Accepted);
}

/// <summary>
/// Judges an unpaired staged detection by where it lands on the model (<see cref="HoldTexturePlacer"/>'s mapping):
/// on an existing hold's placement, on a hold only the 3D photos saw, or off the wall: far off every registered facet AND
/// its ray misses every facet of the model (a facet that did not register must not make its holds "off the wall").
/// A hold seen in 3D wins over "off the wall": contradicting evidence never suggests a discard.
/// </summary>
public static class NewHoldEvidence3D
{
    /// <summary>
    /// A detection this close (mm) to a placed hold's centre is that hold, whatever its size.
    /// <para>
    /// Known trade-off (review 2026-10, F12): the floor absorbs photo-to-model registration error, so it also
    /// catches a genuinely new small hold (a foot chip, a screw-on) set within 35–45 mm of an existing one,
    /// which is then suggested for discard as <see cref="Evidence3DVerdict.KnownHold"/>. Lowering it trades
    /// that for duplicate holds whenever registration drifts. Judge only sees a point, not the detection's
    /// size or 2D footprint, so a size-aware catch needs those passed in first.
    /// </para>
    /// </summary>
    public const double MinimumCatchMm = 35;

    /// <summary>The catch radius of a large hold or a volume is capped, so a new hold bolted onto it is not taken for it.</summary>
    public const double MaximumCatchMm = 45;

    /// <summary>A detection this far (mm) outside every registered facet is not on the wall.</summary>
    public const double OffWallMm = 400;

    /// <summary>The verdict for a detection at a normalised photo point (0..1).</summary>
    public static Evidence3DVerdict Judge(Panel3DEvidence evidence, double x, double y)
    {
        var accepted = evidence.Registrations.Where(r => r.Accepted).ToList();
        if (accepted.Count == 0)
        {
            return Evidence3DVerdict.None;
        }

        var onFacets = new List<(string FacetId, double A, double B)>();
        var mapped = 0;
        var nearestOutsideMm = double.MaxValue;
        foreach (var r in accepted)
        {
            var (a, b) = r.Map(x, y);
            if (!double.IsFinite(a) || !double.IsFinite(b))
            {
                continue;
            }

            mapped++;
            var outside = OutsideMm(r, a, b);
            nearestOutsideMm = Math.Min(nearestOutsideMm, outside);
            if (outside <= HoldTexturePlacer.ExtentMarginMm)
            {
                onFacets.Add((r.FacetId, a, b));
            }
        }

        if (onFacets.Any(p => Near(evidence.SeenIn3D, p)))
        {
            return Evidence3DVerdict.SeenIn3D;
        }

        if (onFacets.Any(p => Near(evidence.KnownHolds, p)))
        {
            return Evidence3DVerdict.KnownHold;
        }

        var offWall = mapped == accepted.Count && nearestOutsideMm >= OffWallMm && evidence.Facets is { } facets
            && PhotoRayFacets.MissesEveryFacet(evidence.Registrations, facets, evidence.Width, evidence.Height, evidence.FocalPx, x, y);
        return offWall ? Evidence3DVerdict.OffWall : Evidence3DVerdict.None;
    }

    /// <summary>Whether a facet point lies within the catch radius of one of <paramref name="spots"/>.</summary>
    /// <param name="spots">The spots.</param>
    /// <param name="facetId">The point's facet.</param>
    /// <param name="a">Plane a, mm.</param>
    /// <param name="b">Plane b, mm.</param>
    /// <returns>True when it does.</returns>
    public static bool IsNear(IReadOnlyList<FacetSpot> spots, string facetId, double a, double b) => Near(spots, (facetId, a, b));

    private static bool Near(IReadOnlyList<FacetSpot> spots, (string FacetId, double A, double B) p) =>
        spots.Any(s => s.FacetId == p.FacetId
            && Math.Sqrt(((s.A - p.A) * (s.A - p.A)) + ((s.B - p.B) * (s.B - p.B))) <= Math.Clamp(s.RadiusMm, MinimumCatchMm, MaximumCatchMm));

    private static double OutsideMm(FacetRegistration r, double a, double b)
    {
        var da = Math.Max(0, Math.Max(r.Extent.AMin - a, a - r.Extent.AMax));
        var db = Math.Max(0, Math.Max(r.Extent.BMin - b, b - r.Extent.BMax));
        return Math.Sqrt((da * da) + (db * db));
    }
}
