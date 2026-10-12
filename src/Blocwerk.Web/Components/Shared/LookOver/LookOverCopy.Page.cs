// <copyright file="LookOverCopy.Page.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>The words around the cards: the wall page's notice, the look-over page's header, group headers and bulk answers.</summary>
internal static partial class LookOverCopy
{
    /// <summary>What settling does to boulders, said once at the top of the page.</summary>
    public const string PageHint =
        "Nothing here changes the wall unless you say so. Answering \"Still there\" or \"Same hold\" clears the mark on boulders "
        + "that were only marked because of that hold; every other boulder marked for review stays with its setter.";

    /// <summary>The wall page's notice for somebody who may look over the wall.</summary>
    public static string BannerHeadline(int open) => open switch
    {
        0 => "This wall was updated. Nothing is open any more.",
        1 => "This wall was updated. 1 finding is waiting for a look-over.",
        _ => $"This wall was updated. {open} findings are waiting for a look-over.",
    };

    /// <summary>The line under <see cref="BannerHeadline"/>: the boulders it concerns, as counted, not as assumed.</summary>
    public static string BannerHint(WallLookOverState state)
    {
        if (state.OpenFindings == 0)
        {
            return "Every finding is answered or was replaced by a later update. Finish the look-over to take this notice off the wall.";
        }

        var marked = state.BouldersMarked == 1 ? "1 boulder is marked for review" : $"{state.BouldersMarked} boulders are marked for review";
        var more = state.MoreBoulders switch
        {
            0 => string.Empty,
            1 => "; 1 more uses a hold the update was unsure about",
            _ => $"; {state.MoreBoulders} more use a hold the update was unsure about",
        };
        return $"Holds the update was unsure about, which nobody has checked yet. {marked}{more}.";
    }

    /// <summary>The look-over page's first line while the wall is flagged.</summary>
    public static string PendingLine(string when, int listed, int open)
    {
        if (open == 0)
        {
            return listed == 0
                ? $"Nothing of the panel update of {when} is open any more. Press Finish look-over to complete it."
                : $"All {PlainCopy.Plural(listed, "finding")} since the panel update of {when} are answered. Press Finish look-over to complete it.";
        }

        return $"The panel update of {when} left findings nobody had checked: {(open == 1 ? "1 is" : $"{open} are")} open"
            + (listed > open ? $", {listed - open} answered." : ".");
    }

    /// <summary>Where the findings come from, said once: "159 findings: 117 from the update's review cards, 18 holds not found again, 24 spots…".</summary>
    public static string? Breakdown(IReadOnlyList<LookOverFindingView> findings)
    {
        if (findings.Count == 0)
        {
            return null;
        }

        var notFound = findings.Count(f => f.Kind == LookOverFindingKind.NotFound);
        var sameSpot = findings.Count(f => f.Kind == LookOverFindingKind.SameSpot);
        var cards = findings.Count - notFound - sameSpot;
        var parts = new List<string>();
        if (cards > 0)
        {
            parts.Add($"{cards} from the update's review cards");
        }

        if (notFound > 0)
        {
            parts.Add(notFound == 1 ? "1 hold on a boulder not found again" : $"{notFound} holds on boulders not found again");
        }

        if (sameSpot > 0)
        {
            parts.Add(sameSpot == 1 ? "1 spot where an old and a new hold now sit together" : $"{sameSpot} spots where an old and a new hold now sit together");
        }

        return $"{PlainCopy.Plural(findings.Count, "finding")}: {string.Join(", ", parts)}.";
    }

    /// <summary>A group header's two counts: "31 on boulders · 91 in all".</summary>
    public static string GroupCounts(int onBoulders, int all) => $"{onBoulders} on boulders · {all} in all";

    /// <summary>The bulk answer of a kind for everything open, or only what no boulder uses.</summary>
    public static string BulkLabel(LookOverFindingKind kind, int count, bool withoutBoulders) => (kind, withoutBoulders) switch
    {
        (LookOverFindingKind.SameSpot, true) => $"Keep both where no boulder uses the old hold ({count})",
        (LookOverFindingKind.SameSpot, false) => $"Keep both everywhere ({count})",
        (_, true) => $"Mark the {count} unsure matches that no boulder uses as same hold",
        _ => $"Mark all {count} unsure matches as same hold",
    };

    /// <summary>What a bulk answer does, asked before it is done.</summary>
    public static string BulkQuestion(LookOverFindingKind kind, int count, bool withoutBoulders)
    {
        var scope = withoutBoulders ? " that no boulder uses" : string.Empty;
        return kind == LookOverFindingKind.SameSpot
            ? $"Keep the old and the new hold on {PlainCopy.Plural(count, "spot")}{scope} without looking at each? No hold is removed or merged; each answer can be undone on its card."
            : $"Mark {(count == 1 ? "1 unsure match" : $"{count} unsure matches")}{scope} as the same hold without looking at each? No hold changes; "
                + "boulders that were only marked because of one of these holds are cleared, and each answer can be undone on its card.";
    }
}
