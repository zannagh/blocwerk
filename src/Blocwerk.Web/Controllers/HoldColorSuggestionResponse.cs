// <copyright file="HoldColorSuggestionResponse.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Controllers;

/// <summary>What <c>POST …/holds/suggest-colors</c> answers.</summary>
/// <param name="Considered">Live holds that had no colour.</param>
/// <param name="Proposed">Holds that got a colour proposed from their photo.</param>
/// <param name="FromLinked">Holds that took the colour of the same hold on another panel.</param>
/// <param name="LeftUnset">Holds left without a colour because the estimate was not sure.</param>
/// <param name="ProposedByColor">The proposals per colour.</param>
/// <param name="LeftUnsetByReason">The holds left unset, per reason.</param>
public sealed record HoldColorSuggestionResponse(
    int Considered,
    int Proposed,
    int FromLinked,
    int LeftUnset,
    IReadOnlyDictionary<string, int> ProposedByColor,
    IReadOnlyDictionary<string, int> LeftUnsetByReason)
{
    /// <summary>Maps the service's report.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The response.</returns>
    public static HoldColorSuggestionResponse From(HoldColorProposalReport report) => new(
        report.Considered,
        report.Proposed,
        report.FromLinked,
        report.LeftUnset,
        report.ProposedByColor,
        report.UnsetByVerdict.ToDictionary(v => v.Key.ToString(), v => v.Value));
}
