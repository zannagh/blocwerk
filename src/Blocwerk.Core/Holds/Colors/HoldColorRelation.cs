// <copyright file="HoldColorRelation.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>How two hold colours relate when asking whether two outlines are the same hold.</summary>
public enum HoldColorRelation
{
    /// <summary>One of the two is not known, or the two are easily mistaken for each other: says nothing.</summary>
    Unknown = 0,

    /// <summary>The same colour.</summary>
    Same = 1,

    /// <summary>Two clearly different colours: the holds are different holds.</summary>
    Different = 2,
}
