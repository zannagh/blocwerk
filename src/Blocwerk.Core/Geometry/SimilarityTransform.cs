namespace Blocwerk.Core.Geometry;

/// <summary>
/// A 2D similarity transform (uniform scale + rotation + translation), written as the complex
/// map z -> a*z + t. Panels are placed with a similarity so that a shared hinge edge can be
/// pinned exactly onto the edge the neighbouring panel already occupies: uniform scale keeps
/// each panel's own aspect ratio (its shape from <see cref="PanelPlane"/>) while resizing it to
/// meet its neighbour, so nothing tears at the hinge.
/// </summary>
public readonly struct SimilarityTransform
{
    private readonly double ar;
    private readonly double ai;
    private readonly double tx;
    private readonly double ty;

    private SimilarityTransform(double scaleRotationReal, double scaleRotationImag, double translateX, double translateY)
    {
        ar = scaleRotationReal;
        ai = scaleRotationImag;
        tx = translateX;
        ty = translateY;
    }

    public static SimilarityTransform Identity => new(1.0, 0.0, 0.0, 0.0);

    public static SimilarityTransform Translation(double dx, double dy) => new(1.0, 0.0, dx, dy);

    /// <summary>
    /// The unique similarity mapping the source segment c0->c1 onto the target segment p0->p1.
    /// If the source segment has collapsed to a point (its projection is edge-on), falls back
    /// to a pure translation pinning c0 onto p0 so the result stays finite and continuous.
    /// </summary>
    public static SimilarityTransform FromEdgeMatch(SchematicPoint c0, SchematicPoint c1, SchematicPoint p0, SchematicPoint p1)
    {
        var cdx = c1.X - c0.X;
        var cdy = c1.Y - c0.Y;
        var denom = (cdx * cdx) + (cdy * cdy);
        if (denom < 1e-18)
        {
            return Translation(p0.X - c0.X, p0.Y - c0.Y);
        }

        var pdx = p1.X - p0.X;
        var pdy = p1.Y - p0.Y;

        // a = (p1 - p0) / (c1 - c0) in complex arithmetic.
        var na = ((pdx * cdx) + (pdy * cdy)) / denom;
        var nb = ((pdy * cdx) - (pdx * cdy)) / denom;

        // t = p0 - a * c0.
        var ntx = p0.X - ((na * c0.X) - (nb * c0.Y));
        var nty = p0.Y - ((nb * c0.X) + (na * c0.Y));
        return new SimilarityTransform(na, nb, ntx, nty);
    }

    public SchematicPoint Apply(SchematicPoint p) =>
        new(((ar * p.X) - (ai * p.Y)) + tx, ((ai * p.X) + (ar * p.Y)) + ty);
}
