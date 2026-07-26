namespace Blocwerk.Core.Enums;

/// <summary>
/// What a hold is made of. Drives which colors are selectable: wooden holds
/// only come in brown tones, everything else uses the full plastic palette.
/// </summary>
public enum HoldMaterial
{
    /// <summary>Polyurethane — the common, lighter plastic hold.</summary>
    PU = 0,

    /// <summary>Polyester — heavier, harder, usually cheaper plastic.</summary>
    PE = 1,

    /// <summary>Dual-texture: shaped grip zones on an otherwise slick surface.</summary>
    DualTex = 2,

    /// <summary>Wood — only available in brown shades.</summary>
    Wood = 3,
}
