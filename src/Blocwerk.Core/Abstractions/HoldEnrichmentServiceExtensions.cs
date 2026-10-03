using Blocwerk.Core.Data;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Abstractions;

/// <summary>The one-line ingest call for <see cref="IHoldEnrichmentService"/>.</summary>
public static class HoldEnrichmentServiceExtensions
{
    /// <summary>
    /// Enriches the request's holds when a service is registered, and swallows (logs) anything it throws:
    /// enrichment is additive and must never fail an ingest.
    /// </summary>
    /// <param name="service">The service, or null when the host registers none.</param>
    /// <param name="db">The caller's context; the caller saves.</param>
    /// <param name="request">The photo, wall and fresh holds.</param>
    /// <param name="logger">The caller's logger, for the failure.</param>
    /// <returns>The run's summary (<see cref="HoldEnrichmentSummary.None"/> when skipped).</returns>
    public static async Task<HoldEnrichmentSummary> EnrichSafelyAsync(
        this IHoldEnrichmentService? service,
        BlocwerkDbContext db,
        HoldEnrichmentRequest request,
        ILogger logger)
    {
        if (service is null)
        {
            return HoldEnrichmentSummary.None;
        }

        try
        {
            return await service.EnrichAsync(db, request);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hold enrichment threw on wall {WallId}; ingest continues without it", request.Wall.Id);
            return HoldEnrichmentSummary.None with { Failed = true };
        }
    }
}
