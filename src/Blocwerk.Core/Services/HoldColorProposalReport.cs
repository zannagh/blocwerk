// <copyright file="HoldColorProposalReport.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Holds.Colors;

namespace Blocwerk.Core.Services;

/// <summary>What one run of the colour proposals did to the holds without a colour.</summary>
public sealed class HoldColorProposalReport
{
    private readonly Dictionary<string, int> proposedByColor = new();
    private readonly Dictionary<HoldColorVerdict, int> unsetByVerdict = new();

    /// <summary>Gets how many holds without a colour were looked at.</summary>
    public int Considered => Proposed + FromLinked + LeftUnset;

    /// <summary>Gets how many holds got a colour proposed from their photo.</summary>
    public int Proposed => proposedByColor.Values.Sum();

    /// <summary>Gets or sets how many holds took the colour of the same hold on another panel.</summary>
    public int FromLinked { get; set; }

    /// <summary>Gets how many holds stayed without a colour because the estimate was not sure.</summary>
    public int LeftUnset => unsetByVerdict.Values.Sum();

    /// <summary>Gets the proposals per palette colour.</summary>
    public IReadOnlyDictionary<string, int> ProposedByColor => proposedByColor;

    /// <summary>Gets the holds left unset, per reason.</summary>
    public IReadOnlyDictionary<HoldColorVerdict, int> UnsetByVerdict => unsetByVerdict;

    /// <summary>Gets a value indicating whether any hold changed.</summary>
    public bool Any => Proposed + FromLinked > 0;

    /// <summary>A one-line summary for the log.</summary>
    /// <returns>The summary.</returns>
    public string Summary() =>
        $"{Considered} holds without a colour: {Proposed} proposed ({Join(proposedByColor)}), {FromLinked} from a linked hold, "
        + $"{LeftUnset} left unset ({Join(unsetByVerdict)})";

    internal void AddProposed(string color, int holds) => proposedByColor[color] = proposedByColor.GetValueOrDefault(color) + holds;

    internal void AddUnset(HoldColorVerdict verdict, int holds) => unsetByVerdict[verdict] = unsetByVerdict.GetValueOrDefault(verdict) + holds;

    private static string Join<T>(Dictionary<T, int> counts)
        where T : notnull =>
        counts.Count == 0 ? "none" : string.Join(", ", counts.OrderBy(c => $"{c.Key}").Select(c => $"{c.Key} {c.Value}"));
}
