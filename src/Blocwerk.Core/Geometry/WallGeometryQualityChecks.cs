// <copyright file="WallGeometryQualityChecks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry;

/// <summary>
/// The solver's self-checks (<c>quality.checks</c>). Every member is optional: older solvers wrote fewer of them,
/// the oldest none at all.
/// </summary>
public sealed record WallGeometryQualityChecks
{
    /// <summary>Human-readable sentences (a folded vertical reference, a borderline facet decision, …).</summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>Facet split decisions within a hair of the fold threshold.</summary>
    public IReadOnlyList<WallGeometryBorderlineDecision>? BorderlineFacetDecisions { get; init; }

    /// <summary>Segment index → measured minus declared angle, degrees (gravity-reference segments excluded).</summary>
    public IReadOnlyDictionary<string, double?>? DeclaredVsMeasuredDeg { get; init; }

    /// <summary>RMS of measured minus printed marker side, mm.</summary>
    public double? MarkerSideRmsErrMm { get; init; }

    /// <summary>Mean of measured minus printed marker side, mm.</summary>
    public double? MarkerSideMeanErrMm { get; init; }

    /// <summary>Marker pairs declared level, with the height difference the solve measured.</summary>
    public IReadOnlyList<WallGeometryLevelPair>? LevelPairs { get; init; }
}

/// <summary>A facet decision close to the fold threshold (<c>quality.checks.borderlineFacetDecisions</c>).</summary>
public sealed record WallGeometryBorderlineDecision
{
    public int Segment { get; init; }

    /// <summary>"split" or the rejected-split kind.</summary>
    public string? Kind { get; init; }

    public double? MinPlaneAngleDeg { get; init; }

    public double? FoldDeg { get; init; }
}

/// <summary>A declared-level marker pair (<c>quality.checks.levelPairs</c>).</summary>
public sealed record WallGeometryLevelPair
{
    public int[] Pair { get; init; } = [];

    public double? HeightDiffMm { get; init; }
}

/// <summary>How gravity was found (<c>quality.gravityDetail</c>); only the parts the app shows are read.</summary>
public sealed record WallGeometryGravityDetail
{
    /// <summary>Declared-vertical segments that turned out folded; gravity came from their whole-segment plane.</summary>
    public IReadOnlyList<WallGeometrySplitReference>? SplitReferences { get; init; }
}

/// <summary>A folded vertical reference (<c>quality.gravityDetail.splitReferences</c>).</summary>
public sealed record WallGeometrySplitReference
{
    public int Segment { get; init; }

    public string? Name { get; init; }

    /// <summary>The angle between the pieces the split was decided on, degrees.</summary>
    public double? FoldDeg { get; init; }

    public double? PieceNormalsAngleDeg { get; init; }

    public string? Method { get; init; }

    public IReadOnlyList<WallGeometrySplitPiece> Pieces { get; init; } = [];
}

/// <summary>One facet of a folded vertical reference.</summary>
public sealed record WallGeometrySplitPiece
{
    public string Facet { get; init; } = string.Empty;

    public IReadOnlyList<int> MarkerIds { get; init; } = [];

    public int? Observations { get; init; }

    public double? ExtentMm { get; init; }

    public double? AngleToSegmentPlaneDeg { get; init; }

    /// <summary>Its weight in the whole-segment plane, 0..1.</summary>
    public double? Share { get; init; }

    /// <summary>Lean against the resulting vertical, degrees (+ = overhang); null when gravity is unknown.</summary>
    public double? LeanDeg { get; init; }
}
