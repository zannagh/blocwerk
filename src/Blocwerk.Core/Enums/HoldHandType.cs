namespace Blocwerk.Core.Enums;

/// <summary>
/// The grip sub-type of a hand hold. Purely descriptive metadata shown to
/// climbers; null means the sub-type is unspecified. Only meaningful for holds
/// whose <see cref="HoldCategory"/> is <see cref="HoldCategory.Hand"/>.
/// </summary>
public enum HoldHandType
{
    /// <summary>A large, easy-to-grip hold.</summary>
    Jug = 0,

    /// <summary>Gripped by squeezing between thumb and fingers.</summary>
    Pinch = 1,

    /// <summary>A small edge held with the fingertips.</summary>
    Crimp = 2,

    /// <summary>A hollow gripped with one or more fingers.</summary>
    Pocket = 3,

    /// <summary>A rounded, friction-dependent hold with no positive edge.</summary>
    Sloper = 4,
}
