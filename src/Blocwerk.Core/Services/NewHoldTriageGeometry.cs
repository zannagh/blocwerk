// <copyright file="NewHoldTriageGeometry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Core.Services;

/// <summary>A footprint on the staged photo, RAW pixels: an outline (absolute vertices) or, without one, a circle.</summary>
/// <param name="X">Centre x.</param>
/// <param name="Y">Centre y.</param>
/// <param name="RadiusPx">Radius (the circle when there is no outline).</param>
/// <param name="Points">The outline's vertices, or null.</param>
/// <param name="Lab">The hold's mean colour (OpenCV 8-bit Lab, from its fingerprint), or null when unknown.</param>
/// <param name="OldHoldId">For an old hold no detection carries: its id (a swap is reported against it); else null.</param>
public readonly record struct TriageOutline(
    double X, double Y, double RadiusPx, IReadOnlyList<(double X, double Y)>? Points = null, TriageLab? Lab = null, Guid? OldHoldId = null);

/// <summary>A mean colour in OpenCV 8-bit Lab (L, a, b each 0..255; a and b centred on 128).</summary>
public readonly record struct TriageLab(double L, double A, double B)
{
    /// <summary>The distance of the two colours' chroma (a/b plane), ignoring lightness: shade and light change L, a swap changes hue.</summary>
    /// <param name="other">The other colour.</param>
    /// <returns>The chroma distance.</returns>
    public double ChromaDistance(TriageLab other) => Math.Sqrt(((A - other.A) * (A - other.A)) + ((B - other.B) * (B - other.B)));

    /// <summary>This colour moved by a shift (a white-balance difference).</summary>
    /// <param name="shift">The shift, or null for none.</param>
    /// <returns>The shifted colour.</returns>
    public TriageLab Plus(TriageLab? shift) => shift is { } d ? new TriageLab(L + d.L, A + d.A, B + d.B) : this;
}

/// <summary>The inside-a-carried-hold verdict on one detection.</summary>
internal enum InsideVerdict
{
    /// <summary>Not inside a carried hold (or a hold sitting on a volume).</summary>
    Outside,

    /// <summary>A second outline or a part of a carried hold.</summary>
    Inside,

    /// <summary>Inside a carried hold but of a clearly different colour: another hold now sits on its spot.</summary>
    Swapped,
}

/// <summary>
/// The footprints and markers one panel's triage compares its detections with: the carried holds' footprints on the
/// staged photo, and the markers the OLD photo showed, mapped onto the staged photo through the matched pairs (a marker
/// the staged photo did not decode — too oblique, too dark — is still a marker, not a new hold).
/// </summary>
internal sealed class NewHoldTriageGeometry
{
    /// <summary>A detection at least this much inside carried footprints is a second outline or a part of a carried hold.</summary>
    public const double InsideCarriedShare = 0.5;

    /// <summary>
    /// From this chroma distance (a/b plane of 8-bit Lab) on, a detection inside a carried hold is another hold, not that one
    /// again. On The Attic's gen 5 → 6 update the duplicates and parts inside a carried hold differ by at most 15, the purple
    /// hold that replaced an orange one by 35.
    /// </summary>
    public const double SwapChromaDistance = 20;

    /// <summary>A swap is a hold of about the carried one's size: the detection covers at least this share of its footprint.</summary>
    public const double SwapMinCover = 0.4;

    private readonly List<(HoldArea Area, TriageOutline Outline)> carried;
    private readonly List<IReadOnlyList<(double X, double Y)>> oldMarkers;
    private readonly TriageLab? colourShift;

    private NewHoldTriageGeometry(
        List<(HoldArea Area, TriageOutline Outline)> carriedAreas, List<IReadOnlyList<(double X, double Y)>> oldMarkerQuads, TriageLab? shift)
    {
        carried = carriedAreas;
        oldMarkers = oldMarkerQuads;
        colourShift = shift;
    }

    /// <summary>Builds the geometry of <paramref name="input"/>; the old markers are only mapped when the panel is aligned.</summary>
    /// <param name="input">The panel's triage input.</param>
    /// <param name="aligned">Whether the staged photo was aligned with the old one.</param>
    /// <returns>The geometry.</returns>
    public static NewHoldTriageGeometry For(NewHoldTriageInput input, bool aligned)
    {
        var areas = (input.CarriedOutlines ?? []).Select(o => (Area(o), o)).Where(a => a.Item1.Area > 0).ToList();
        var markers = new List<IReadOnlyList<(double X, double Y)>>();
        if (aligned && input.OldMarkerQuads is { Count: > 0 } quads)
        {
            var oldToNew = input.NewToOld.Select(p => new PointPair(p.DstX, p.DstY, p.SrcX, p.SrcY)).ToList();
            foreach (var quad in quads)
            {
                var mapped = quad.Select(c => LocalAffine.Predict(oldToNew, c.X, c.Y)).ToList();
                if (mapped.All(m => m is not null))
                {
                    markers.Add(mapped.Select(m => m!.Value).ToList());
                }
            }
        }

        return new NewHoldTriageGeometry(areas, markers, input.OldToNewColourShift);
    }

    /// <summary>The footprint as a <see cref="HoldArea"/> in RAW pixels.</summary>
    /// <param name="o">The footprint.</param>
    /// <returns>The area.</returns>
    public static HoldArea Area(TriageOutline o) =>
        new(o.X, o.Y, o.RadiusPx, o.Points is { Count: >= 3 } p ? p.Select(v => new ShapePoint { Dx = v.X - o.X, Dy = v.Y - o.Y }).ToList() : null, null);

    /// <summary>The candidate's footprint: its outline when it has one, else its circle.</summary>
    /// <param name="c">The candidate.</param>
    /// <returns>The area (no extent when the size is unknown).</returns>
    public static HoldArea Area(TriageCandidate c) => Area(new TriageOutline(c.X, c.Y, c.RadiusPx, c.Outline));

    /// <summary>True when the candidate's centre lies on a marker the old photo showed.</summary>
    /// <param name="c">The candidate.</param>
    /// <returns>Whether it does.</returns>
    public bool OnOldMarker(TriageCandidate c) => oldMarkers.Any(q => NewHoldTriage.InsideGrown(q, c.X, c.Y));

    /// <summary>
    /// Inside when at least <see cref="InsideCarriedShare"/> of the candidate lies inside the footprints of carried holds: a
    /// second outline of a hold that is already there, or a part of one (a bolt hole, a screw, a facet). Exceptions: a
    /// confident detection that sits on a clearly larger OUTLINED carried hold (<see cref="HoldStacking"/>) is a hold bolted
    /// onto a volume; and one whose colour differs clearly from the carried hold it mostly lies in
    /// (<see cref="SwapChromaDistance"/>) is another hold on that spot (<see cref="InsideVerdict.Swapped"/>).
    /// </summary>
    /// <param name="c">The candidate.</param>
    /// <param name="swappedWith">For a swap, the carried hold it lies in.</param>
    /// <returns>The verdict.</returns>
    public InsideVerdict InsideCarried(TriageCandidate c, out TriageOutline? swappedWith)
    {
        swappedWith = null;
        var area = Area(c);
        var under = area.Area <= 0 ? [] : carried.Where(u => area.MayTouch(u.Area)).ToList();
        if (under.Count == 0 || area.CoveredBy(under.Select(u => u.Area)) < InsideCarriedShare)
        {
            return InsideVerdict.Outside;
        }

        if (c.Confident && under.Any(u => HoldStacking.SitsOn(area, u.Area)))
        {
            return InsideVerdict.Outside;
        }

        var main = under.MaxBy(u => area.CoveredBy([u.Area]));
        if (IsSwap(c, area, main))
        {
            swappedWith = main.Outline;
            return InsideVerdict.Swapped;
        }

        return InsideVerdict.Inside;
    }

    /// <summary>
    /// Another hold on the carried hold's spot by colour and size: the chroma differs by <see cref="SwapChromaDistance"/> (an
    /// old hold's colour, measured on the old photo, shifted by the captures' white-balance difference first) and the
    /// detection covers at least <see cref="SwapMinCover"/> of the carried footprint (a bolt hole or facet is far smaller).
    /// </summary>
    private bool IsSwap(TriageCandidate c, HoldArea area, (HoldArea Area, TriageOutline Outline) main)
    {
        if (c.Lab is not { } lab || main.Outline.Lab is not { } carriedLab || main.Area.Area <= 0)
        {
            return false;
        }

        var expected = main.Outline.OldHoldId is null ? carriedLab : carriedLab.Plus(colourShift);
        var coverOfCarried = area.CoveredBy([main.Area]) * area.Area / main.Area.Area;
        return lab.ChromaDistance(expected) >= SwapChromaDistance && coverOfCarried >= SwapMinCover;
    }
}
