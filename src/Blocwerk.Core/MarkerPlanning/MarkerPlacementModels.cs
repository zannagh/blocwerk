// <copyright file="MarkerPlacementModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// "Did I place the markers right?" — the plan compared with what a solve measured. Stored on the
/// capture as JSON and shown with its result.
/// </summary>
/// <param name="PlannedMarkers">Markers in the plan.</param>
/// <param name="SolvedMarkers">Planned markers the solve placed.</param>
/// <param name="CheckedMarkers">Markers whose position could be compared (≥ 2 solved on the same surface).</param>
/// <param name="Findings">Everything that does not match, most serious first.</param>
public sealed record MarkerPlacementCheck(
    [property: JsonPropertyName("plannedMarkers")] int PlannedMarkers,
    [property: JsonPropertyName("solvedMarkers")] int SolvedMarkers,
    [property: JsonPropertyName("checkedMarkers")] int CheckedMarkers,
    [property: JsonPropertyName("findings")] IReadOnlyList<MarkerPlacementFinding> Findings)
{
    /// <summary>True when nothing was found.</summary>
    [JsonIgnore]
    public bool AllGood => Findings.Count == 0;
}

/// <summary>One mismatch between plan and solve.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="MarkerId">The marker concerned, if any.</param>
/// <param name="PlannedSegment">The segment the plan puts it (or the angle) on.</param>
/// <param name="ObservedSegment">The segment the solve found it on.</param>
/// <param name="Value">
/// Offset in mm (<see cref="MarkerPlacementIssue.Offset"/>), angle difference in degrees, or measured minus planned
/// size in mm (<see cref="MarkerPlacementIssue.SizeMismatch"/>).
/// </param>
/// <param name="Message">Plain-language description for the admin.</param>
public sealed record MarkerPlacementFinding(
    [property: JsonPropertyName("kind")] MarkerPlacementIssue Kind,
    [property: JsonPropertyName("markerId")] int? MarkerId,
    [property: JsonPropertyName("plannedSegment")] int? PlannedSegment,
    [property: JsonPropertyName("observedSegment")] int? ObservedSegment,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Kinds of placement findings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MarkerPlacementIssue>))]
public enum MarkerPlacementIssue
{
    /// <summary>The marker sits on a different surface than planned.</summary>
    WrongSegment,

    /// <summary>The marker is on the planned surface but far from its planned spot.</summary>
    Offset,

    /// <summary>The marker was never seen in any photo.</summary>
    NeverSeen,

    /// <summary>The marker was seen but the solve could not place it.</summary>
    NotSolved,

    /// <summary>A surface's measured angle differs clearly from the planned one.</summary>
    AngleMismatch,

    /// <summary>The marker measures clearly larger or smaller in the photos than its planned printed size.</summary>
    SizeMismatch,
}
