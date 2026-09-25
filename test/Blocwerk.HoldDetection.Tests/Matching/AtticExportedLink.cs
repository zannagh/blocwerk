namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>One row of a wall export's <c>links.json</c>: two holds that are the same physical hold on overlapping panels.</summary>
/// <param name="HoldAId">One hold.</param>
/// <param name="HoldBId">The other.</param>
internal sealed record AtticExportedLink(Guid HoldAId, Guid HoldBId);
