// <copyright file="LookOverCopy.Deck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>The words of the card deck: the filter chips, a card's short lines and the bar after an answer.</summary>
internal static partial class LookOverCopy
{
    /// <summary>The chip of the holds the update could not find.</summary>
    public const string MissingChip = "Could not find";

    /// <summary>A group's name on its filter chip: the short form of <see cref="GroupTitle"/>.</summary>
    public static string ChipLabel(LookOverFindingKind kind) => kind switch
    {
        LookOverFindingKind.ReplacedByOther => "Other hold here",
        LookOverFindingKind.NotFound => "Not found again",
        LookOverFindingKind.PossiblyRemoved => "Possibly removed",
        LookOverFindingKind.UnsureMatch => "Unsure matches",
        LookOverFindingKind.SuspectedMove => "May have moved",
        LookOverFindingKind.SameSpot => "Same spot",
        LookOverFindingKind.MatchedToHandPlaced => "Hand-placed, merged",
        LookOverFindingKind.HandPlacedAmbiguous => "Hand-placed, several",
        LookOverFindingKind.DiscardedDetection => DiscardedChip,
        LookOverFindingKind.UnsureLink => "Linked, unsure",
        _ => "Conflicting new",
    };

    /// <summary>What a card is about, in the queue list beside the deck and above a card.</summary>
    public static string QueueLabel(LookOverFindingView f) => f.Missing ? MissingChip : f.ProbablyDuplicate ? DuplicatesChip : ChipLabel(f.Kind);

    /// <summary>The hold a card is about, named as the cards name it.</summary>
    public static string HoldLabel(LookOverFindingView f) => f.HoldName is { } name ? $"The {name} hold" : "This hold";

    /// <summary>The collapsed line about a card's boulders.</summary>
    public static string UsedBy(int boulders) => boulders switch
    {
        0 => "No boulder uses it",
        1 => "Used by 1 boulder",
        _ => $"Used by {boulders} boulders",
    };

    /// <summary>The header's finish button while findings are open.</summary>
    public static string FinishAnyway(int open) => $"Finish anyway — {open} open";

    /// <summary>What finishing with open findings means, asked before it is done.</summary>
    public static string FinishAnywayQuestion(int open) =>
        $"{(open == 1 ? "1 finding is" : $"{open} findings are")} still open. They stay listed on this page, but the wall is no longer shown as waiting for a look-over.";

    /// <summary>The deck when its filter has nothing open.</summary>
    public static string NothingOpen(bool filtered, bool forOneBoulder)
    {
        if (forOneBoulder)
        {
            return "No finding on a hold of this boulder is open.";
        }

        return filtered ? "Nothing is open in this group. Pick another one above." : "Every finding is answered.";
    }
}
