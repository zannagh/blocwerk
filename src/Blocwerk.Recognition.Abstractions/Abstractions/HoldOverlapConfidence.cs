// <copyright file="HoldOverlapConfidence.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>What an overlap proposal's confidence says beyond its ranking, shared by the matcher and the code reading its proposals.</summary>
public static class HoldOverlapConfidence
{
    /// <summary>
    /// The ceiling of a proposal with no measured anchor (texture, marker or wall-space correspondence) near it: the matcher caps
    /// such proposals here, so a confidence above it says the pair is backed by measured evidence.
    /// </summary>
    public const double UnsupportedCap = 0.40;
}
