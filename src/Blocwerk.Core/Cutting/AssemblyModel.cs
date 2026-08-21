using System.Globalization;

namespace Blocwerk.Core.Helpers;

/// <summary>A single 3D edge of the assembled piece, for the wireframe front page.</summary>
public readonly record struct AssemblyEdge(Point3D A, Point3D B);

/// <summary>A labelled key dimension shown alongside the assembly drawing.</summary>
public readonly record struct AssemblyDimension(string Label, string Value);

/// <summary>
/// The assembled piece as an isometric wireframe plus its key dimensions, used for
/// the front page of the cutting-plan PDF.
/// </summary>
public sealed record AssemblyModel(
    string Title,
    IReadOnlyList<AssemblyEdge> Edges,
    IReadOnlyList<AssemblyDimension> Dimensions);

/// <summary>Builds <see cref="AssemblyModel"/> instances from calculator results.</summary>
public static class AssemblyModels
{
    public static AssemblyModel FromVolume(string title, VolumeResult result)
    {
        var edges = new List<AssemblyEdge>();
        var baseVerts = result.BaseVertices;

        for (var i = 0; i < baseVerts.Length; i++)
        {
            edges.Add(new AssemblyEdge(baseVerts[i], baseVerts[(i + 1) % baseVerts.Length]));
        }

        if (result.RidgeVertices is { Length: 2 } ridge)
        {
            edges.Add(new AssemblyEdge(ridge[0], ridge[1]));
            for (var i = 0; i < baseVerts.Length; i++)
            {
                var nearest = Dist(baseVerts[i], ridge[0]) <= Dist(baseVerts[i], ridge[1]) ? ridge[0] : ridge[1];
                edges.Add(new AssemblyEdge(baseVerts[i], nearest));
            }
        }
        else
        {
            foreach (var v in baseVerts)
            {
                edges.Add(new AssemblyEdge(v, result.Apex));
            }

            if (result.BottomApex is { } bottom)
            {
                foreach (var v in baseVerts)
                {
                    edges.Add(new AssemblyEdge(v, bottom));
                }
            }
        }

        var dims = new List<AssemblyDimension>
        {
            new("Dihedral", $"{F(result.DihedralAngleDeg)}°"),
            new("Base bevel", $"{F(result.BaseBevelAngleDeg)}°"),
            new("Miter", $"{F(result.MiterAngleDeg)}°"),
            new("Slant height", $"{F(result.SlantHeight)} mm"),
        };

        return new AssemblyModel(title, edges, dims);
    }

    public static AssemblyModel FromWedge(string title, WedgeResult result, double faceWidth)
    {
        var cross = result.CrossSection;
        var edges = new List<AssemblyEdge>();

        Point3D Front(Point2D p) => new(p.X, p.Y, 0);
        Point3D Back(Point2D p) => new(p.X, p.Y, faceWidth);

        for (var i = 0; i < cross.Length; i++)
        {
            var a = cross[i];
            var b = cross[(i + 1) % cross.Length];
            edges.Add(new AssemblyEdge(Front(a), Front(b)));
            edges.Add(new AssemblyEdge(Back(a), Back(b)));
            edges.Add(new AssemblyEdge(Front(a), Back(a)));
        }

        var dims = new List<AssemblyDimension>
        {
            new("Tip from wall", $"{F(result.DepthMm)} mm"),
            new("Wall footprint", $"{F(result.WallFootprintMm)} mm"),
            new("Overall width", $"{F(result.OverallWidthMm)} mm"),
            new("Angle change", $"{F(result.AngleChangeDeg)}°"),
        };

        return new AssemblyModel(title, edges, dims);
    }

    private static double Dist(Point3D a, Point3D b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
