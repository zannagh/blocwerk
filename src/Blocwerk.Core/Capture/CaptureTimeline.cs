// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>One stage of a capture's timeline: when it started, when it ended and how.</summary>
/// <param name="Stage">A <see cref="CaptureTimeline"/> stage key (e.g. <c>solving</c>, <c>rerender</c>).</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="EndedAt">When it ended (null: it still runs).</param>
/// <param name="Outcome"><see cref="CaptureTimeline.Done"/> or <see cref="CaptureTimeline.Failed"/> once ended.</param>
public sealed record CaptureTimelineEntry(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("endedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? EndedAt = null,
    [property: JsonPropertyName("outcome"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Outcome = null)
{
    /// <summary>How long it ran, once ended.</summary>
    [JsonIgnore]
    public TimeSpan? Duration => EndedAt - StartedAt;
}

/// <summary>
/// The stage timeline stored on <see cref="WallCapture.TimelineJson"/>. Three lanes run independently: the pipeline (one
/// stage per running <see cref="WallCaptureStatus"/>), a texture re-render (<see cref="CaptureTextureOutcome.RerenderMark"/>)
/// and a re-solve (<see cref="CaptureResolveMark"/>). Facts only (times and outcomes), never prose: the progress API derives
/// elapsed times and historical stage durations from it.
/// </summary>
public static partial class CaptureTimeline
{
    /// <summary>The stage ended as planned.</summary>
    public const string Done = "done";

    /// <summary>The stage ended in a failure.</summary>
    public const string Failed = "failed";

    /// <summary>The texture re-render lane.</summary>
    public const string Rerender = "rerender";

    /// <summary>The re-solve lane.</summary>
    public const string Resolve = "resolve";

    /// <summary>How many entries a capture keeps (the newest).</summary>
    internal const int MaxEntries = 40;

    private static readonly JsonSerializerOptions Json = new();

    /// <summary>The pipeline stage key of a running status, or null for a status that is not a running stage.</summary>
    public static string? StageOf(WallCaptureStatus status) => status switch
    {
        WallCaptureStatus.Queued => "queued",
        WallCaptureStatus.Detecting => "detecting",
        WallCaptureStatus.Solving => "solving",
        WallCaptureStatus.Texturing => "texturing",
        WallCaptureStatus.Splatting => "splatting",
        _ => null,
    };

    /// <summary>Reads a stored timeline; empty when there is none or it does not parse.</summary>
    public static List<CaptureTimelineEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CaptureTimelineEntry>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The timeline as stored, the newest <see cref="MaxEntries"/> entries only.</summary>
    public static string ToJson(List<CaptureTimelineEntry> entries) =>
        JsonSerializer.Serialize(entries.Count > MaxEntries ? entries[^MaxEntries..] : entries, Json);

    /// <summary>The open (running) entry of <paramref name="stage"/>, or null.</summary>
    public static CaptureTimelineEntry? Open(IReadOnlyList<CaptureTimelineEntry> entries, string stage) =>
        entries.LastOrDefault(e => e.Stage == stage && e.EndedAt is null);

    /// <summary>The pipeline moved from <paramref name="from"/> to <paramref name="to"/>: closes the old stage, opens the new one.</summary>
    internal static bool OnStatus(List<CaptureTimelineEntry> entries, WallCaptureStatus? from, WallCaptureStatus to, DateTimeOffset now)
    {
        var changed = false;
        foreach (var open in entries.Where(e => e.EndedAt is null && IsPipeline(e.Stage)).ToList())
        {
            changed |= Close(entries, open.Stage, now, PipelineOutcome(open.Stage, to));
        }

        if (StageOf(to) is { } stage)
        {
            entries.Add(new CaptureTimelineEntry(stage, now));
            changed = true;
        }

        return changed || from != to;
    }

    /// <summary>A side lane (<see cref="Rerender"/>, <see cref="Resolve"/>) started or ended.</summary>
    internal static bool OnLane(List<CaptureTimelineEntry> entries, string lane, bool wasRunning, bool isRunning, bool succeeded, DateTimeOffset now)
    {
        if (wasRunning == isRunning)
        {
            return false;
        }

        if (isRunning)
        {
            Close(entries, lane, now, Failed);
            entries.Add(new CaptureTimelineEntry(lane, now));
            return true;
        }

        return Close(entries, lane, now, succeeded ? Done : Failed);
    }

    private static bool IsPipeline(string stage) => stage is not (Rerender or Resolve);

    /// <summary>A capture that failed failed its stage; a photo-real stage that ended without the view failed too.</summary>
    private static string PipelineOutcome(string stage, WallCaptureStatus to) =>
        to == WallCaptureStatus.Failed || (stage == "splatting" && to == WallCaptureStatus.SucceededWithoutSplat) ? Failed : Done;

    private static bool Close(List<CaptureTimelineEntry> entries, string stage, DateTimeOffset now, string outcome)
    {
        var index = entries.FindLastIndex(e => e.Stage == stage && e.EndedAt is null);
        if (index < 0)
        {
            return false;
        }

        entries[index] = entries[index] with { EndedAt = now, Outcome = outcome };
        return true;
    }
}
