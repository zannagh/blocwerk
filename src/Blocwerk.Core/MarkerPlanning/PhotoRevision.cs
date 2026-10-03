// <copyright file="PhotoRevision.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>The revision tag stored with a photo's marker observations.</summary>
/// <param name="Revision">The revision the photo shows (null: no plan, the legacy ids).</param>
/// <param name="From">The lowest compatible revision (null: not inferred).</param>
/// <param name="To">The highest compatible revision (null: not inferred).</param>
public sealed record PhotoRevision(int? Revision, int? From, int? To)
{
    /// <summary>The inferred tag, or — without evidence (no plan) — <paramref name="current"/> as before.</summary>
    public static PhotoRevision Of(MarkerRevisionEvidence? evidence, int? current) =>
        evidence is null
            ? new PhotoRevision(current, null, null)
            : new PhotoRevision(evidence.Revision, evidence.CompatibleFrom, evidence.CompatibleTo);
}
