// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Enums;

/// <summary>
/// The style tags a boulder can carry, any combination of them. A fixed set stored as one integer column: the values are
/// bit flags and must never be renumbered, only appended to.
/// </summary>
[Flags]
public enum BoulderStyle
{
    /// <summary>No style assigned yet.</summary>
    None = 0,

    /// <summary>Body tension: staying tight between holds, compression and lock-offs.</summary>
    Tension = 1,

    /// <summary>Dynamic moves: dynos and swings.</summary>
    Dynamic = 2,

    /// <summary>Funky: unusual, coordination-heavy movement.</summary>
    Funky = 4,

    /// <summary>Endurance: long or pumpy.</summary>
    Endurance = 8,

    /// <summary>Power: few, hard moves.</summary>
    Power = 16,

    /// <summary>Technical: footwork and precision.</summary>
    Technical = 32,
}
