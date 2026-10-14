// <copyright file="RedetectOutcomes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Holds;
using Blocwerk.Core.Redetect;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The wording of the wall editor's re-detect: the one confirm that says what it will do to the holds of the panel, and
/// the line shown afterwards. Each sentence states one thing the service really does (<see cref="IPanelRedetectService"/>).
/// </summary>
internal static class RedetectOutcomes
{
    /// <summary>Merged pairs are named up to this many; beyond it only the count is given.</summary>
    public const int MaxNamedMerges = 5;

    private const string Review = "Wall settings › Possible duplicate holds";

    /// <summary>What applying the plan does. Red when holds are removed, which cannot be taken back here.</summary>
    public static HoldActionOutcome Confirm(PanelRedetectSummary summary)
    {
        var holds = new List<string>();
        Add(holds, summary.BetterShapes, "hold gets", "holds get", "the newly detected shape and position. Name, colour, boulders and links stay.");
        Add(holds, summary.FilledOutlines, "hand-placed hold without an outline gets", "hand-placed holds without an outline get", "the detected outline and stay where they were placed.");
        Add(holds, summary.NewHolds, "new hold is", "new holds are", "added with a suggested colour.");
        if (summary.Merges.Count > 0)
        {
            holds.Add($"{PlainCopy.Plural(summary.Merges.Count, "pair")} of the same hold recorded twice {IsAre(summary.Merges.Count)} merged into one ({Named(summary.Merges)}).");
        }

        Add(holds, summary.Removed, "detected hold that was not found again and that no boulder uses is", "detected holds that were not found again and that no boulder uses are", "removed.");

        var rest = new List<string>();
        if (summary.Merges.Count > 0)
        {
            rest.Add("The boulders of a merged hold move to the hold that stays.");
        }

        Add(rest, summary.HandShapesKept, "shape drawn or placed by hand stays", "shapes drawn or placed by hand stay", "untouched.");
        rest.Add("Holds that boulders use are never removed; virtual and hidden holds are not touched.");
        Add(rest, summary.ForReview, "more pair that looks alike is", "more pairs that look alike are", $"not merged and left for {Review}.");

        var undo = summary.Merges.Count > 0
            ? $"Each merge can be undone under {Review} › Recent merges. The other changes cannot be undone here."
            : "This cannot be undone here.";
        return new HoldActionOutcome(string.Join(" ", holds), string.Join(" ", rest), undo, Danger: summary.Removed > 0);
    }

    /// <summary>The line for a run that leaves everything as it is.</summary>
    public static string NothingToDo(PanelRedetectSummary summary) => summary.Detected == 0
        ? "Re-detect found no holds on this photo. Nothing was changed."
        : $"Re-detect found {PlainCopy.Plural(summary.Detected, "hold")}, all of them already there. Nothing was changed.";

    /// <summary>The line shown after applying.</summary>
    public static string Done(PanelRedetectSummary summary)
    {
        var parts = new List<string>();
        Add(parts, summary.BetterShapes + summary.FilledOutlines, "shape updated", "shapes updated", string.Empty);
        Add(parts, summary.NewHolds, "new hold", "new holds", string.Empty);
        Add(parts, summary.Merges.Count, "pair merged", "pairs merged", string.Empty);
        Add(parts, summary.Removed, "hold removed", "holds removed", string.Empty);
        Add(parts, summary.Stacked, "hold recorded as sitting on another", "holds recorded as sitting on another", string.Empty);
        Add(parts, summary.FailedMerges, "merge not done", "merges not done", string.Empty);
        Add(parts, summary.Stale.Count, "answer left out because its hold changed since you looked", "answers left out because their holds changed since you looked", string.Empty);
        return parts.Count == 0 ? "Re-detect changed nothing" : $"Re-detect: {string.Join(", ", parts)}";
    }

    private static void Add(List<string> sentences, int count, string one, string many, string tail)
    {
        if (count > 0)
        {
            sentences.Add($"{count:N0} {(count == 1 ? one : many)} {tail}".TrimEnd());
        }
    }

    private static string IsAre(int count) => count == 1 ? "is" : "are";

    /// <summary>"red, “Jug”, blue" or, beyond <see cref="MaxNamedMerges"/>, "red, blue, … and 3 more".</summary>
    private static string Named(IReadOnlyList<RedetectMergeInfo> merges)
    {
        var names = merges.Take(MaxNamedMerges).Select(Label).ToList();
        var more = merges.Count - names.Count;
        return more > 0 ? $"{string.Join(", ", names)} and {more:N0} more" : string.Join(", ", names);
    }

    private static string Label(RedetectMergeInfo merge)
    {
        if (!string.IsNullOrWhiteSpace(merge.Name))
        {
            return $"“{merge.Name}”";
        }

        return string.IsNullOrWhiteSpace(merge.Color) ? "no colour" : HoldPalette.DisplayName(merge.Color).ToLowerInvariant();
    }
}
