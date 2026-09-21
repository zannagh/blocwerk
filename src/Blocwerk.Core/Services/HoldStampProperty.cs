// <copyright file="HoldStampProperty.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// Which of a hold's appearance properties the pipette carries. Each is an INDEPENDENT tick in the
/// pipette's contextual row, so "copy just the colour" and "copy everything but the size" are both
/// one gesture — and an unticked property is never written to the hold that is stamped.
/// <para>
/// A flags enum rather than six booleans because the set is passed around as one value (the buffer,
/// the toolbar row, the produced <see cref="HoldEdit"/>) and every one of those would otherwise have
/// grown the same six parameters.
/// </para>
/// </summary>
[Flags]
public enum HoldStampProperty
{
    /// <summary>Nothing ticked: a pipette tap then copies nothing and stamping is a no-op.</summary>
    None = 0,

    /// <summary>The hold's radius.</summary>
    Size = 1,

    /// <summary>The hold's colour key, constrained by material — see <see cref="HoldStamp"/>.</summary>
    Color = 2,

    /// <summary>PU / PE / DualTex / Wood.</summary>
    Material = 4,

    /// <summary>Hand or foot.</summary>
    Category = 8,

    /// <summary>The hand sub-type (crimp, jug, sloper …).</summary>
    HandType = 16,

    /// <summary>Whether the hold sits on the kickboard.</summary>
    Kickboard = 32,

    /// <summary>Every property the pipette knows how to carry.</summary>
    All = Size | Color | Material | Category | HandType | Kickboard,
}
