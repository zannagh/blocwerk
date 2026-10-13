// <copyright file="HoldSamenessVerdict.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Redetect;

/// <summary>Whether two outlines on one photo are the same physical hold.</summary>
public enum HoldSamenessVerdict
{
    /// <summary>Not in the same place, or too different in size.</summary>
    Different = 0,

    /// <summary>A much smaller hold inside a larger one: a hold on a volume. Two holds.</summary>
    Stacked = 1,

    /// <summary>In the same place, but two clearly different colours. Two holds.</summary>
    ColorClash = 2,

    /// <summary>In the same place and compatible, but not sure enough to merge two stored holds without asking.</summary>
    Likely = 3,

    /// <summary>The same hold, sure enough to merge without asking.</summary>
    Same = 4,
}
