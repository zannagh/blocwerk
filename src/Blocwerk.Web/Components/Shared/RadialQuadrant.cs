namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The four choices of the radial classify overlay used by the new boulder-setting experience.
/// Each maps to a hold classification: <see cref="Top"/>/<see cref="Start"/>/<see cref="Middle"/>
/// are hand holds (which implicitly serve as feet), <see cref="Foot"/> is a dedicated foothold.
/// </summary>
public enum RadialQuadrant
{
    /// <summary>Upper quadrant — the finish (top) hold.</summary>
    Top,

    /// <summary>Lower quadrant — a start hold.</summary>
    Start,

    /// <summary>Right quadrant — a normal (middle) hand hold.</summary>
    Middle,

    /// <summary>Left quadrant — a dedicated foothold.</summary>
    Foot,
}
