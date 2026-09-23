using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>One photo's outline proposals and the measurements of its newly outlined holds.</summary>
/// <param name="Proposals">One per eligible hold.</param>
/// <param name="Metrics">Millimetre sizes keyed by the proposal's hold snapshot (empty off marker walls).</param>
internal sealed record OutlineUpgradePhotoPlan(List<HoldOutlineUpgradeProposal> Proposals, Dictionary<Hold, HoldMetric> Metrics);
