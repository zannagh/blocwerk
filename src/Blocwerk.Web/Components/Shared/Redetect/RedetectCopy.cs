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
        RedetectProposalKind.NewHold => "It is not added unless you accept. Accepting adds it with a suggested colour.",
        RedetectProposalKind.NotFound => "No boulder uses it and nobody named or coloured it. Accepting removes it.",
        RedetectProposalKind.HandShape => "Accepting replaces your shape with the detected one. Name, colour and boulders stay. Its 3D shape is made again.",
        _ => "Accepting gives the hold the detected shape and position. Name, colour and boulders stay. Its 3D shape is made again.",
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
        RedetectProposalKind.NewHold => "Do not add",
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

    /// <summary>Why the detector is unsure, in one sentence.</summary>
    public static string Why(RedetectDoubt doubt) => doubt switch
    {
        RedetectDoubt.DropsPart => "The new shape leaves out part of the old one.",
        RedetectDoubt.MuchLarger => "The new shape is much larger than the old one.",
        RedetectDoubt.ThinPart => "The new shape is a thin strip or has a thin tail.",
        RedetectDoubt.TakesInAnotherHold => "The new shape reaches over another hold.",
        RedetectDoubt.PaleHold => "The hold hardly stands out from the wall.",
        RedetectDoubt.PiecesMissPart => "The pieces leave out part of the hold.",
        _ => string.Empty,
    };

    /// <summary>The state of a card, beside its heading.</summary>
    public static string State(RedetectProposal proposal, bool? answer) => answer switch
    {
        true => "Accepted",
        false => proposal.Kind == RedetectProposalKind.NewHold ? "Not added" : "Kept as it is",
        _ => "Not answered",
    };

    /// <summary>The label of the step that accepts the safe shape updates together.</summary>
    public static string BulkLabel(int count) => $"Accept the {count:N0} safe shape {(count == 1 ? "update" : "updates")}";

    /// <summary>The label of a filter chip.</summary>
    public static string FilterLabel(RedetectFilter filter) => filter switch
    {
        RedetectFilter.Open => "Not answered",
        RedetectFilter.Safe => "Safe",
        RedetectFilter.Unsure => "Unsure",
        RedetectFilter.Shape => "Shape",
        RedetectFilter.YourShape => "Your shape",
        RedetectFilter.Split => "Two holds in one",
        RedetectFilter.Merge => "Same hold twice",
        RedetectFilter.Stack => "Hold on a hold",
        RedetectFilter.NewHold => "New hold",
        RedetectFilter.NotFound => "Not found",
        _ => "All",
    };

    /// <summary>The count under the cards.</summary>
    public static string Progress(int answered, int total, int accepted) =>
        $"{answered:N0} of {total:N0} answered, {accepted:N0} accepted";

    /// <summary>The two sentences above the cards.</summary>
    public static string Intro(PanelRedetectPlan plan) =>
        $"{PlainCopy.Plural(plan.Proposals.Count, "proposal")} for this photo. Nothing changes until you apply.";

    /// <summary>What the review does not show, or null when nothing was left out.</summary>
    public static string? LeftOut(PanelRedetectPlan plan)
    {
        var parts = new List<string>();
        if (plan.TurnedDown > 0)
        {
            parts.Add($"{PlainCopy.Plural(plan.TurnedDown, "detection")} on spots where somebody already said there is no hold.");
        }

        if (plan.Untraced > 0)
        {
            parts.Add($"{PlainCopy.Plural(plan.Untraced, "detection")} on an empty spot without a traceable outline.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
