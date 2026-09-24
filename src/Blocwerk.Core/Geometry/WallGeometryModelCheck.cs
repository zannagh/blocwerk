// <copyright file="WallGeometryModelCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry;

/// <summary>One plain-language line about a solved model, for the wall admin.</summary>
/// <param name="Kind">What it is about: one of the <c>Kind…</c> constants.</param>
/// <param name="Level"><see cref="Info"/> or <see cref="Warning"/>; the solver never reports a failure here.</param>
/// <param name="Message">The sentence to show.</param>
public sealed record WallGeometryModelCheck(string Kind, string Level, string Message)
{
    /// <summary>Something worth knowing; nothing to fix.</summary>
    public const string Info = "info";

    /// <summary>Worth a look; the model was still made.</summary>
    public const string Warning = "warning";

    /// <summary>A segment's measured angle (next to its declared one).</summary>
    public const string KindSegmentAngle = "segment-angle";

    /// <summary>A sentence the solver wrote into <c>quality.checks.warnings</c>.</summary>
    public const string KindSolverWarning = "solver-warning";

    /// <summary>A folded vertical reference (older documents without warnings).</summary>
    public const string KindSplitReference = "split-reference";

    /// <summary>A facet decision close to the fold threshold (older documents without warnings).</summary>
    public const string KindBorderlineFacet = "borderline-facet";

    /// <summary>No vertical reference: absolute angles were not measured.</summary>
    public const string KindGravity = "gravity";

    /// <summary>A declared-level marker pair.</summary>
    public const string KindLevelPair = "level-pair";

    /// <summary>The printed marker size against the measured one.</summary>
    public const string KindMarkerSize = "marker-size";

    /// <summary>A detection the solver removed as a false one.</summary>
    public const string KindRejectedObservation = "rejected-observation";
}
