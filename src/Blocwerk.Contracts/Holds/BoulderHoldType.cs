// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Enums;

/// <summary>
/// The prominent hold types of a boulder, any combination of them: the five grips and the two foot hooks. Stored as one
/// integer column, so the values are bit flags and must never be renumbered, only appended to. Unlike a hold's
/// <see cref="HoldHandType"/>, the hooks describe how a boulder is climbed, so only a person sets them.
/// </summary>
[Flags]
public enum BoulderHoldType
{
    /// <summary>None set; the boulder's holds decide.</summary>
    None = 0,

    /// <summary>Jugs.</summary>
    Jug = 1,

    /// <summary>Crimps.</summary>
    Crimp = 2,

    /// <summary>Pinches.</summary>
    Pinch = 4,

    /// <summary>Slopers.</summary>
    Sloper = 8,

    /// <summary>Pockets.</summary>
    Pocket = 16,

    /// <summary>Heel hooks.</summary>
    HeelHook = 32,

    /// <summary>Toe hooks.</summary>
    ToeHook = 64,
}
