// <copyright file="RedetectFilters.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Redetect;

/// <summary>Which cards of the re-detect review are shown.</summary>
public enum RedetectFilter
{
    /// <summary>Every card.</summary>
    All,

    /// <summary>Cards without an answer.</summary>
    Open,

    /// <summary>The shape updates that can be accepted together and have no answer yet.</summary>
    Safe,

    /// <summary>Cards the detector is unsure about.</summary>
    Unsure,

    /// <summary>A different shape for an automatic hold.</summary>
    Shape,

    /// <summary>A different shape for a hold a person placed or drew.</summary>
    YourShape,

    /// <summary>Two holds under one outline.</summary>
    Split,

    /// <summary>The same hold recorded twice.</summary>
    Merge,

    /// <summary>A hold on a hold.</summary>
    Stack,

    /// <summary>A new hold.</summary>
    NewHold,

    /// <summary>A hold that was not found again.</summary>
    NotFound,

    /// <summary>A new hold on a spot that was answered before ("Not a hold", set aside, or a deleted hold).</summary>
    AnsweredBefore,
}

/// <summary>Which proposal belongs to which filter, and which cards start out answered "keep". Pure.</summary>
public static class RedetectFilters
{
    /// <summary>True when the card starts out answered "keep": the detector is unsure, or it would add a hold.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <returns>Whether it is kept unless a person accepts it.</returns>
    public static bool StartsKept(RedetectProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return proposal.Unsure || proposal.Kind == RedetectProposalKind.NewHold;
    }

    /// <summary>
    /// The safe shape updates that have no answer yet: what the bulk step accepts, and what its button and the "Safe"
    /// chip count. One rule for both, so the two numbers never differ.
    /// </summary>
    /// <param name="proposals">The proposals.</param>
    /// <param name="answer">The answer per proposal so far; null without one.</param>
    /// <returns>The proposals the bulk step would accept.</returns>
    public static List<RedetectProposal> SafeToAccept(IEnumerable<RedetectProposal> proposals, Func<RedetectProposal, bool?> answer)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(answer);
        return [.. proposals.Where(p => Matches(RedetectFilter.Safe, p, answer(p)))];
    }

    /// <summary>True when the proposal is shown under the filter.</summary>
    /// <param name="filter">The filter.</param>
    /// <param name="proposal">The proposal.</param>
    /// <param name="answer">Its answer so far; null without one.</param>
    /// <returns>Whether it matches.</returns>
    public static bool Matches(RedetectFilter filter, RedetectProposal proposal, bool? answer)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return filter switch
        {
            RedetectFilter.Open => answer is null,
            RedetectFilter.Safe => proposal.Safe && answer is null,
            RedetectFilter.Unsure => proposal.Unsure,
            RedetectFilter.Shape => proposal.Kind == RedetectProposalKind.BetterShape,
            RedetectFilter.YourShape => proposal.Kind == RedetectProposalKind.HandShape,
            RedetectFilter.Split => proposal.Kind == RedetectProposalKind.Split,
            RedetectFilter.Merge => proposal.Kind == RedetectProposalKind.Merge,
            RedetectFilter.Stack => proposal.Kind == RedetectProposalKind.Stack,
            RedetectFilter.NewHold => proposal.Kind == RedetectProposalKind.NewHold,
            RedetectFilter.NotFound => proposal.Kind == RedetectProposalKind.NotFound,
            RedetectFilter.AnsweredBefore => proposal.Earlier is not null,
            _ => true,
        };
    }
}
