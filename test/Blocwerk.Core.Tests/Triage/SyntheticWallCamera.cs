// <copyright file="SyntheticWallCamera.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// A pinhole camera (4000 × 3000 px, f = 3000 px) 4 m in front of <see cref="HoldPlacementScenario.TwoFacetJson"/>'s
/// facet "0" (the plane y = 0, a = x, b = z), at (1500, −4000, 1500) looking along +y. Facet "1" folds away at x = 2000.
/// Only facet "0" is registered, with its exact photo → plane mapping.
/// </summary>
internal sealed class SyntheticWallCamera
{
    public const int Width = 4000;
    public const int Height = 3000;
    public const double Focal = 3000;

    private static readonly double[] Centre = [1500, -4000, 1500];

    private readonly WallGeometryDocument doc = WallGeometryDocument.Parse(HoldPlacementScenario.TwoFacetJson);

    /// <summary>The normalised photo point of a world point (mm).</summary>
    public (double X, double Y) Project(double wx, double wy, double wz)
    {
        var (dx, dy, dz) = (wx - Centre[0], wy - Centre[1], wz - Centre[2]);
        return (((Focal * dx / dy) + (Width / 2.0)) / Width, ((Focal * -dz / dy) + (Height / 2.0)) / Height);
    }

    /// <summary>The normalised photo point of facet "1"'s plane point (a, b).</summary>
    public (double X, double Y) OnFacet1(double a, double b) => Project(2000 + (0.8 * a), 0.6 * a, b);

    public FacetRegistration Registration()
    {
        var pairs = new List<PointCorrespondence>();
        foreach (var a in new[] { 0.0, 1000, 2000 })
        {
            foreach (var b in new[] { 0.0, 1500, 3000 })
            {
                var (x, y) = Project(a, 0, b);
                pairs.Add(new PointCorrespondence(x, y, a, b));
            }
        }

        var h = PlaneHomography.Fit(pairs)!;
        return new FacetRegistration(
            "0", true, 200, 150, 80, 0.8, 0.6, 1, null, h, Extents()["0"], Math.Sign(h.Depth(0.5, 0.5)));
    }

    public Panel3DEvidence Evidence() => new([Registration()], [], [], Facets(), Width, Height, Focal);

    private Dictionary<string, PlaneRectMm> Extents() => Wall3DFallbackPlacement.FacetExtents(doc);

    private Dictionary<string, ModelFacet> Facets()
    {
        var extents = Extents();
        return doc.Segments.SelectMany(s => s.Facets)
            .ToDictionary(f => f.Id!, f => new ModelFacet(FacetFrame.From(f)!, extents[f.Id!]));
    }
}
