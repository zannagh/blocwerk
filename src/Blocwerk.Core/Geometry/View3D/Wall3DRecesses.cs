// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Finds the enclosed recesses of a wall from its facets alone: a pair of triangular facets that face each other,
/// stand perpendicular to the main wall and each have a hypotenuse (two corners) on the main wall's plane, with
/// the third corner behind it (the triangle lies in the solid behind an overhang's 45° plane). The pair encloses a
/// pocket behind the plane: closed by the two triangles, their vertical and top legs, and the plane between the
/// hypotenuses, the attic's roof surface (<see cref="Wall3DRecess"/>).
/// </summary>
public static class Wall3DRecesses
{
    /// <summary>A triangle's normal this near perpendicular to the main wall's counts as standing on it.</summary>
    private const double PerpendicularDot = 0.15;

    /// <summary>Hypotenuse corners this close to the main plane lie on it.</summary>
    private const double OnPlaneMm = 40;

    /// <summary>The triangle's third corner at least this far behind the main plane.</summary>
    private const double ApexOffMm = 150;

    /// <summary>Facing triangles at most this far apart form a recess (a slot, not two far-off panels).</summary>
    private const double MaxGapMm = 3000;

    private const double FacingDot = -0.9;

    /// <summary>The recesses of <paramref name="facets"/>; empty without a main wall or a qualifying pair.</summary>
    public static IReadOnlyList<Wall3DRecess> Find(IReadOnlyList<Wall3DFacet> facets)
    {
        var main = MainFacet(facets);
        if (main is null)
        {
            return [];
        }

        var tris = facets
            .Where(f => f.Id != main.Id && f.Corners.Count == 3)
            .Select(f => RecessTriangle.Of(f, main, PerpendicularDot, OnPlaneMm, ApexOffMm))
            .OfType<RecessTriangle>()
            .ToList();
        var found = new List<Wall3DRecess>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tris.Count; i++)
        {
            for (var j = i + 1; j < tris.Count; j++)
            {
                if (used.Contains(tris[i].Facet.Id) || used.Contains(tris[j].Facet.Id) || !Faces(tris[i], tris[j]))
                {
                    continue;
                }

                found.Add(Build(main, tris[i], tris[j]));
                used.Add(tris[i].Facet.Id);
                used.Add(tris[j].Facet.Id);
            }
        }

        return found;
    }

    /// <summary>The main wall as the viewer takes it: facet "0", else the largest.</summary>
    public static Wall3DFacet? MainFacet(IReadOnlyList<Wall3DFacet> facets) =>
        facets.FirstOrDefault(f => f.Id == "0") ?? facets.OrderByDescending(Area).FirstOrDefault();

    private static bool Faces(RecessTriangle a, RecessTriangle b)
    {
        if (Vec.Dot(a.Facet.Normal, b.Facet.Normal) > FacingDot)
        {
            return false;
        }

        var gap = Vec.Dot(a.Facet.Normal, Vec.Sub(b.Facet.Corners[0], a.Facet.Corners[0]));
        return gap is > 1 and < MaxGapMm && Vec.Dot(b.Facet.Normal, Vec.Sub(a.Facet.Corners[0], b.Facet.Corners[0])) > 1;
    }

    private static Wall3DRecess Build(Wall3DFacet main, RecessTriangle a, RecessTriangle b)
    {
        var centre = Centroid(main.Corners);
        var (left, closing) = Vec.Length(Vec.Sub(Centroid(a.Facet.Corners), centre)) <= Vec.Length(Vec.Sub(Centroid(b.Facet.Corners), centre))
            ? (a, b)
            : (b, a);
        var dir = Vec.Unit(Vec.Sub(left.Hypotenuse[1], left.Hypotenuse[0]));
        var (l0, l1) = Order(left.Hypotenuse, dir);
        var (c0, c1) = Order(closing.Hypotenuse, dir);
        var planes = new List<double[]>
        {
            Outward(left.Facet.Normal, left.Facet.Corners[0]),
            Outward(closing.Facet.Normal, closing.Facet.Corners[0]),
            new[] { main.Normal[0], main.Normal[1], main.Normal[2], Vec.Dot(main.Normal, main.Origin) },
        };
        planes.AddRange(left.LegPlanes());
        planes.AddRange(closing.LegPlanes());
        return new Wall3DRecess(main.Id, left.Facet.Id, closing.Facet.Id, [l0, l1, c1, c0], planes);
    }

    /// <summary>The half-space behind a closing triangle (its normal flipped): the recess lies on the triangle's front.</summary>
    private static double[] Outward(double[] normal, double[] point) =>
        [-normal[0], -normal[1], -normal[2], -Vec.Dot(normal, point)];

    private static (double[] Lo, double[] Hi) Order(double[][] pair, double[] dir) =>
        Vec.Dot(pair[0], dir) <= Vec.Dot(pair[1], dir) ? (pair[0], pair[1]) : (pair[1], pair[0]);

    private static double[] Centroid(IReadOnlyList<double[]> pts) =>
        [pts.Average(p => p[0]), pts.Average(p => p[1]), pts.Average(p => p[2])];

    private static double Area(Wall3DFacet f)
    {
        var sum = new double[3];
        for (var i = 1; i < f.Corners.Count - 1; i++)
        {
            var c = Vec.Cross(Vec.Sub(f.Corners[i], f.Corners[0]), Vec.Sub(f.Corners[i + 1], f.Corners[0]));
            for (var k = 0; k < 3; k++)
            {
                sum[k] += c[k];
            }
        }

        return Vec.Length(sum);
    }
}
