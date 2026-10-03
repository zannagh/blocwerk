namespace Blocwerk.Core.Enums;

/// <summary>
/// How a hold's outline (<see cref="Entities.Hold.ShapePoints"/>, or the plain radius circle) came about.
/// </summary>
public enum HoldOutlineSource
{
    /// <summary>Drawn or edited by a person.</summary>
    Manual = 0,

    /// <summary>Automatically detected; only a centre and radius are known (no contour).</summary>
    AutoCircle = 1,

    /// <summary>Automatically detected with a traced contour.</summary>
    AutoContour = 2,
}
