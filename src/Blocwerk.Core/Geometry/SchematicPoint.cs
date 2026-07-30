namespace Blocwerk.Core.Geometry;

/// <summary>
/// A 2D point in schematic (unfolded map) space, using the same normalized 0..1 axes as
/// photo space. A genuine unfolding moves points in both axes, so the schematic API returns
/// this rather than a bare corrected Y.
/// </summary>
public readonly record struct SchematicPoint(double X, double Y)
{
    public SchematicPoint Translate(double dx, double dy) => new(X + dx, Y + dy);
}
