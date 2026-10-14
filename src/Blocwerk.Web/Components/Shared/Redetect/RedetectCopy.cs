// <copyright file="RedetectCopy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Redetect;

namespace Blocwerk.Web.Components.Shared.Redetect;

/// <summary>The words of the re-detect review: what each proposal is, and what its two answers do.</summary>
internal static class RedetectCopy
{
    /// <summary>The answer that leaves the wall as it is.</summary>
    public const string KeepLabel = "Keep mine";

    /// <summary>The line on a card whose detected shape is likely worse than the stored one.</summary>
    public const string Unsure =
        "The detector is unsure here: the new shape leaves out part of the old one, is much larger, has a thin tail, or reaches over another hold. It stays as it is unless you accept.";

    /// <summary>The caption of the picture that shows the wall as it is stored.</summary>
    public const string NowCaption = "On the wall now";

    /// <summary>The caption of the picture that shows what accepting gives.</summary>
    public const string ThenCaption = "If you accept";

    /// <summary>What the proposal is, as the card's heading.</summary>
    public static string Heading(RedetectProposal proposal) => proposal.Kind switch
    {
        RedetectProposalKind.Split => $"The detector sees {proposal.Detections.Count:N0} holds here",
        RedetectProposalKind.Merge => "These two look like the same hold recorded twice",
        RedetectProposalKind.Stack => "This hold sits on the larger one",
        RedetectProposalKind.NewHold => "A hold that is not on the wall yet",
        RedetectProposalKind.NotFound => "This hold was not found again",
        RedetectProposalKind.HandShape => "A different shape for a hold you placed or drew",
        _ => "A different shape for this hold",
    };

    /// <summary>What accepting does, in one sentence under the pictures.</summary>
    public static string Detail(RedetectProposal proposal) => proposal.Kind switch
    {
        RedetectProposalKind.Split =>
            "Accepting gives the stored hold the shape of the largest one and adds the others as new holds. Name, colour and boulders stay with the stored hold.",
        RedetectProposalKind.Merge => "Accepting merges them into one hold. The boulders of both move to it. A merge can be undone in the duplicate review.",
        RedetectProposalKind.Stack => "Accepting records that it is mounted on the larger hold. Both stay two holds.",
        RedetectProposalKind.NewHold => "Accepting adds it with a suggested colour.",
        RedetectProposalKind.NotFound => "No boulder uses it and nobody named or coloured it. Accepting removes it.",
        RedetectProposalKind.HandShape => "Accepting replaces your shape with the detected one. Name, colour and boulders stay.",
        _ => "Accepting gives the hold the detected shape and position. Name, colour and boulders stay.",
    };

    /// <summary>The label of the accepting answer.</summary>
    public static string AcceptLabel(RedetectProposal proposal) => proposal.Kind switch
    {
        RedetectProposalKind.Split => "Split it",
        RedetectProposalKind.Merge => "Merge them",
        RedetectProposalKind.Stack => "Record it",
        RedetectProposalKind.NewHold => "Add the hold",
        RedetectProposalKind.NotFound => "Remove it",
        _ => "Accept",
    };

    /// <summary>The label of the answer that changes nothing.</summary>
    public static string KeepLabelFor(RedetectProposal proposal) => proposal.Kind switch
    {
        RedetectProposalKind.NewHold => "Not a hold",
        RedetectProposalKind.NotFound => "Keep it",
        RedetectProposalKind.Merge or RedetectProposalKind.Stack => "Leave as it is",
        _ => KeepLabel,
    };

    /// <summary>The short kind shown beside "3 of 40".</summary>
    public static string Kind(RedetectProposal proposal) => proposal.Kind switch
    {
        RedetectProposalKind.Split => "two holds in one",
        RedetectProposalKind.Merge => "same hold twice",
        RedetectProposalKind.Stack => "hold on a hold",
        RedetectProposalKind.NewHold => "new hold",
        RedetectProposalKind.NotFound => "not found",
        RedetectProposalKind.HandShape => "your shape",
        _ => "shape",
    };

    /// <summary>The line above the cards: how much there is to look at, and what was left out.</summary>
    public static string Intro(PanelRedetectPlan plan)
    {
        var safe = plan.Proposals.Count(p => p.Safe);
        var parts = new List<string>
        {
            $"{PlainCopy.Plural(plan.Proposals.Count, "proposal")}. Nothing changes until you apply your answers. A hold that gets a new outline gets its 3D shape made again afterwards, in the background.",
        };
        if (safe > 0)
        {
            parts.Add($"{PlainCopy.Plural(safe, "shape update")} keep the hold where and about as large as it is and can be accepted together.");
        }

        var unsure = plan.Proposals.Count(p => p.Unsure);
        if (unsure > 0)
        {
            parts.Add($"On {PlainCopy.Plural(unsure, "card")} the detector is unsure; {(unsure == 1 ? "it stays" : "they stay")} as {(unsure == 1 ? "it is" : "they are")} unless you accept {(unsure == 1 ? "it" : "them")}.");
        }

        if (plan.TurnedDown > 0)
        {
            parts.Add($"{PlainCopy.Plural(plan.TurnedDown, "detection")} on spots where somebody already said there is no hold {(plan.TurnedDown == 1 ? "was" : "were")} left out.");
        }

        if (plan.Untraced > 0)
        {
            parts.Add($"{PlainCopy.Plural(plan.Untraced, "detection")} on an empty spot without a traceable outline {(plan.Untraced == 1 ? "was" : "were")} left out as well.");
        }

        return string.Join(" ", parts);
    }
}
