namespace Blocwerk.Core.Geometry;

/// <summary>
/// The linear part of a single planar panel's projection into the schematic.
/// <para>
/// A panel is a flat plane in 3D whose orientation is given by two angles: an inclination
/// (tilt away from vertical about a horizontal axis) and a yaw (rotation about the vertical
/// axis, i.e. how far it is turned to face sideways). With the camera looking straight at the
/// wall (orthographic, dropping depth), the panel's own horizontal axis u and vertical axis v
/// project onto the image as:
/// </para>
/// <code>
///   u -> (cos yaw,               0     )
///   v -> (-sin yaw * sin incl,   cos incl)
/// </code>
/// <para>
/// so a photo-space delta (dx, dy) measured on the panel maps to the schematic by the matrix
/// M = [[cos yaw, -sin yaw*sin incl], [0, cos incl]]. At yaw = 0 this collapses to the legacy
/// behaviour: x is untouched and y is squashed by cos(incl).
/// </para>
/// </summary>
public readonly struct PanelPlane
{
    private readonly double m00;
    private readonly double m01;
    private readonly double m11;

    private PanelPlane(double a00, double a01, double a11)
    {
        m00 = a00;
        m01 = a01;
        m11 = a11;
    }

    /// <summary>
    /// Builds the projection for a panel with the given inclination and yaw in degrees.
    /// </summary>
    public static PanelPlane FromDegrees(double inclinationDegrees, double yawDegrees)
    {
        var incl = inclinationDegrees * Math.PI / 180.0;
        var yaw = yawDegrees * Math.PI / 180.0;
        return new PanelPlane(
            Math.Cos(yaw),
            -Math.Sin(yaw) * Math.Sin(incl),
            Math.Cos(incl));
    }

    /// <summary>The identity plane: faces the camera, no foreshortening.</summary>
    public static PanelPlane Identity => new(1.0, 0.0, 1.0);

    /// <summary>
    /// Applies the linear projection to an absolute photo-space point. The result still needs
    /// a per-panel placement (translation/rotation/scale) before it is a schematic position.
    /// </summary>
    public SchematicPoint Apply(double x, double y) =>
        new((m00 * x) + (m01 * y), m11 * y);
}
