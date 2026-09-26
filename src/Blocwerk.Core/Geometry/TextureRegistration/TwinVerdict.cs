// <copyright file="TwinVerdict.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>Which side of a linked pair of placements is wrong.</summary>
public enum TwinVerdict
{
    /// <summary>The two placements agree.</summary>
    Agree,

    /// <summary>They disagree and the first one's evidence is clearly worse.</summary>
    FirstWrong,

    /// <summary>They disagree and the second one's evidence is clearly worse.</summary>
    SecondWrong,

    /// <summary>They disagree and neither side's evidence is clearly better: both are dropped.</summary>
    BothWrong,
}
