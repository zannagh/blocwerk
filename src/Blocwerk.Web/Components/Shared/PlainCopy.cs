// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text.RegularExpressions;
using Blocwerk.Core.Jobs;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The one place that turns internal keys, ids and counts into plain language for the admin screens: job step names,
/// job states, capture ids inside stored notes, plurals and hold positions.
/// </summary>
internal static partial class PlainCopy
{
    private static readonly Dictionary<string, string> StepNames = new(StringComparer.Ordinal)
    {
        ["queued"] = "Waiting for a free slot",
        ["detecting"] = "Finding markers in the photos",
        ["solving"] = "Measuring the wall",
        ["texturing"] = "Painting wall textures",
        ["splatting"] = "Training the photo-real view",
        ["uploading"] = "Uploading",
        ["measure-protrusion"] = "Measuring hold depth",
        ["place-holds"] = "Placing holds on the 3D model",
        ["suggest-hold-links"] = "Looking for holds seen on two photos",
        ["detect-volumes"] = "Finding volumes",
        ["refine-footprints"] = "Refining hold shapes in 3D",
        ["find-hold-proposals"] = "Looking for new holds",
        ["coverage-report"] = "Checking photo coverage",
        ["rerender"] = "Painting wall textures again",
        ["resolve"] = "Measuring the wall again",
    };

    [GeneratedRegex(@"capture [0-9a-f]{32}", RegexOptions.IgnoreCase)]
    private static partial Regex CaptureIdPattern();

    [GeneratedRegex(@"\s*\(job [^)]*\)")]
    private static partial Regex JobIdPattern();

    /// <summary>A job step or stage key in words; unknown keys fall back to the key with dashes as spaces.</summary>
    public static string StepName(string key) =>
        StepNames.TryGetValue(key, out var name) ? name : key.Replace('-', ' ');

    /// <summary>A job state in words ("succeeded" reads as "Done").</summary>
    public static string StateName(string state) => state switch
    {
        JobStates.Succeeded => "Done",
        JobStates.Running => "Running",
        JobStates.Queued => "Waiting",
        JobStates.Failed => "Failed",
        JobStates.Skipped => "Nothing to do",
        JobStates.Cancelled => "Cancelled",
        _ => state,
    };

    /// <summary>The stage chip of a job, or null when it would only repeat the state (or says nothing).</summary>
    public static string? StageName(string? stage, string state)
    {
        if (string.IsNullOrEmpty(stage) || string.Equals(stage, state, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = StepName(stage);
        return string.Equals(name, StateName(state), StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>Stored notes without internal ids: "capture d760f1…" reads "an earlier capture", the job id goes.</summary>
    public static string Notes(string notes) =>
        JobIdPattern().Replace(CaptureIdPattern().Replace(notes, "an earlier capture"), string.Empty);

    /// <summary>"1 hold" or "3 holds".</summary>
    public static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");

    /// <summary>A spot on a photo or facet as "near 47% across, 55% down" (x and y are 0..1).</summary>
    public static string Position(double x, double y) =>
        string.Create(CultureInfo.InvariantCulture, $"near {Math.Round(x * 100)}% across, {Math.Round(y * 100)}% down");
}
