namespace Blocwerk.Core.Helpers;

/// <summary>
/// Tolerant comparisons for the geometry maths. A coordinate that is mathematically zero — a
/// panel edge lying in the base plane, two vertices at the same height — arrives as the rounding
/// residue of a projection or a rotation, so exact equality misclassifies it.
/// </summary>
public static class FloatCompare
{
    /// <summary>
    /// Millimetre-scale model space: a micrometre of disagreement is the same coordinate. Well
    /// below anything a cut can hold, well above the residue of a few chained trig operations.
    /// </summary>
    public const double GeometryEpsilon = 1e-6;

    /// <summary>
    /// Normalised (0..1) wall coordinates, where a millimetre on a five-metre wall is already 2e-4.
    /// </summary>
    public const double NormalisedEpsilon = 1e-9;

    /// <summary>True when the two values agree to within <paramref name="epsilon"/>.</summary>
    public static bool AboutEqual(double a, double b, double epsilon = GeometryEpsilon)
    {
        return Math.Abs(a - b) <= epsilon;
    }

    /// <summary>True when the value is zero to within <paramref name="epsilon"/>.</summary>
    public static bool AboutZero(double value, double epsilon = GeometryEpsilon)
    {
        return Math.Abs(value) <= epsilon;
    }

    /// <summary>True when <paramref name="a"/> is smaller than <paramref name="b"/> by more than the tolerance.</summary>
    public static bool Below(double a, double b, double epsilon = GeometryEpsilon)
    {
        return a < b - epsilon;
    }

    /// <summary>True when <paramref name="a"/> is larger than <paramref name="b"/> by more than the tolerance.</summary>
    public static bool Above(double a, double b, double epsilon = GeometryEpsilon)
    {
        return a > b + epsilon;
    }
}
