// <copyright file="RedetectFilters.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Redetect;

namespace Blocwerk.Web.Components.Shared.Redetect;

/// <summary>Which cards of the re-detect review are shown.</summary>
public enum RedetectFilter
{
    /// <summary>Every card.</summary>
    All,

    /// <summary>Cards without an answer.</summary>
    Open,

    /// <summary>The shape updates that can be accepted together.</summary>
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
            RedetectFilter.Safe => proposal.Safe,
            RedetectFilter.Unsure => proposal.Unsure,
            RedetectFilter.Shape => proposal.Kind == RedetectProposalKind.BetterShape,
            RedetectFilter.YourShape => proposal.Kind == RedetectProposalKind.HandShape,
            RedetectFilter.Split => proposal.Kind == RedetectProposalKind.Split,
            RedetectFilter.Merge => proposal.Kind == RedetectProposalKind.Merge,
            RedetectFilter.Stack => proposal.Kind == RedetectProposalKind.Stack,
            RedetectFilter.NewHold => proposal.Kind == RedetectProposalKind.NewHold,
            RedetectFilter.NotFound => proposal.Kind == RedetectProposalKind.NotFound,
            _ => true,
        };
    }
}
