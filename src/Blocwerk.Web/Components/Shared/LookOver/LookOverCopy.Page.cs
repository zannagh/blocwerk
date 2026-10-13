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
        "Nothing here changes the wall unless you say so, and what you change in the hold editor is picked up here. "
        + "Boulders marked for review mostly stay marked: a mark is only cleared for a boulder that was marked solely because holds "
        + "were not found again or were unsure matches, once all of those are answered \"Still here\" or \"Same hold\".";

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
        var missing = state.MissingHolds switch
        {
            0 => string.Empty,
            1 => " 1 hold was not found on the new photos and is not drawn on the wall.",
            _ => $" {state.MissingHolds} holds were not found on the new photos and are not drawn on the wall.",
        };
        return $"Holds the update was unsure about, which nobody has checked yet. {marked}{more}.{missing}";
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

    /// <summary>Where the findings come from, said once: "285 findings: 183 holds the update could not find, 102 from the update's review cards".</summary>
    public static string? Breakdown(IReadOnlyList<LookOverFindingView> findings)
    {
        if (findings.Count == 0)
        {
            return null;
        }

        var missing = findings.Count(f => f.KeptAtUpdate);
        var sameSpot = findings.Count(f => f is { KeptAtUpdate: false, Kind: LookOverFindingKind.SameSpot });
        var discarded = findings.Count(f => f.Discarded);
        var cards = findings.Count - missing - sameSpot - discarded;
        var parts = new List<string>();
        if (missing > 0)
        {
            parts.Add(missing == 1 ? "1 hold the update could not find" : $"{missing} holds the update could not find");
        }

        if (cards > 0)
        {
            parts.Add($"{cards} from the update's review cards");
        }

        if (sameSpot > 0)
        {
            parts.Add(sameSpot == 1 ? "1 spot where an old and a new hold now sit together" : $"{sameSpot} spots where an old and a new hold now sit together");
        }

        if (discarded > 0)
        {
            parts.Add(discarded == 1 ? "1 detection the update discarded" : $"{discarded} detections the update discarded");
        }

        return $"{PlainCopy.Plural(findings.Count, "finding")}: {string.Join(", ", parts)}.";
    }

    /// <summary>
    /// Under a group's title: how many of the kind the view lists against how many there are, with the whole kind's
    /// open count when the badge beside it (what is open in THIS view) is not the whole story.
    /// </summary>
    public static string GroupSub(int listed, (int OnBoulders, int All, int Open) counts, bool onBouldersOnly, bool forOneBoulder)
    {
        if (forOneBoulder)
        {
            return $"{listed} for this boulder · {counts.All} in all, {counts.Open} open";
        }

        return onBouldersOnly && counts.All > counts.OnBoulders
            ? $"{counts.OnBoulders} on boulders · {counts.All} in all, {counts.Open} open"
            : $"{counts.OnBoulders} on boulders · {counts.All} in all";
    }

    /// <summary>The bulk answer of a kind, saying exactly how many findings it settles and which.</summary>
    public static string BulkLabel(LookOverFindingKind kind, int count, LookOverBulkScope scope) => (kind, scope) switch
    {
        (LookOverFindingKind.SameSpot, LookOverBulkScope.WithoutBoulders) => $"Keep both where no boulder uses the old hold ({count})",
        (LookOverFindingKind.SameSpot, LookOverBulkScope.OnBoulders) => $"Keep both on the {count} spots a boulder uses",
        (LookOverFindingKind.SameSpot, _) => $"Keep both everywhere ({count})",
        (_, LookOverBulkScope.WithoutBoulders) => $"Mark the {count} unsure matches that no boulder uses as same hold",
        (_, LookOverBulkScope.OnBoulders) => $"Mark the {count} unsure matches on boulders as same hold",
        _ => $"Mark all {count} unsure matches as same hold",
    };

    /// <summary>Said after a bulk answer that found fewer findings open than were confirmed; null when it did all of them.</summary>
    public static string? BulkShortfall(LookOverBulkResult result) => result.Settled >= result.Asked
        ? null
        : $"{result.Settled} of {result.Asked} were answered now; the others had been answered meanwhile, or a boulder uses their hold by now.";

    /// <summary>What a bulk answer does, asked before it is done.</summary>
    public static string BulkQuestion(LookOverFindingKind kind, int count, LookOverBulkScope which)
    {
        var scope = which switch
        {
            LookOverBulkScope.WithoutBoulders => " that no boulder uses",
            LookOverBulkScope.OnBoulders => " that a boulder uses",
            _ => string.Empty,
        };
        return kind == LookOverFindingKind.SameSpot
            ? $"Keep the old and the new hold on {PlainCopy.Plural(count, "spot")}{scope} without looking at each? No hold is removed or merged; each answer can be undone on its card."
            : $"Mark {(count == 1 ? "1 unsure match" : $"{count} unsure matches")}{scope} as the same hold without looking at each? No hold changes; "
                + "boulders that were only marked because of one of these holds are cleared, and each answer can be undone on its card.";
    }
}
