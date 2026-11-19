// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State;

/// <summary>Small page rules of the boulder page and its full editor, kept out of the components so they can be tested.</summary>
public static class BoulderReviseRules
{
    /// <summary>
    /// Whether the Now / Then pills are offered: only for a boulder that needs a look (flagged for review, or historic
    /// after a wall rebuild), and while "Then" is up so it can be left. Needs an older state to show at all.
    /// </summary>
    /// <param name="hasThen">The boulder has an older state to show.</param>
    /// <param name="needsReview">The boulder is flagged for review.</param>
    /// <param name="isHistoric">The boulder is historic.</param>
    /// <param name="showingThen">The "Then" view is currently up.</param>
    /// <returns>Whether to render the pills.</returns>
    public static bool OfferGenerationToggle(bool hasThen, bool needsReview, bool isHistoric, bool showingThen) =>
        hasThen && (needsReview || isHistoric || showingThen);

    /// <summary>
    /// Whether a save from the full editor replaces the boulder's holds (and with them signs off a review). A draft or
    /// historic boulder always does; a live one only when its holds were edited or the review was confirmed from the
    /// review notice. Otherwise the save is metadata only.
    /// </summary>
    /// <param name="isDraft">The boulder is a draft.</param>
    /// <param name="isHistoric">The boulder is historic.</param>
    /// <param name="reviewConfirmed">The editor was opened from the review notice.</param>
    /// <param name="holdsEdited">The selection differs from the pre-selection.</param>
    /// <returns>Whether holds are sent.</returns>
    public static bool ReplacesHolds(bool isDraft, bool isHistoric, bool reviewConfirmed, bool holdsEdited) =>
        isDraft || isHistoric || reviewConfirmed || holdsEdited;
}
