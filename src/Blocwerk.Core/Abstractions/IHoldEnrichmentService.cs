using Blocwerk.Core.Data;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Post-detection enrichment of freshly detected holds at ingest: real outlines + fingerprints on every
/// wall, and — only on walls with <c>Wall.GlyphsEnabled</c> — ArUco marker observations and metric
/// (mm) hold sizes / plane positions. Never throws: on any failure the holds are left exactly as
/// detection produced them and the failure is logged.
/// </summary>
public interface IHoldEnrichmentService
{
    /// <summary>
    /// Enriches <see cref="HoldEnrichmentRequest.Holds"/> in place and stages marker observations on
    /// <paramref name="db"/>. Does not save: the caller's SaveChanges commits everything together.
    /// </summary>
    /// <param name="db">The caller's context (observations and the active geometry model go through it).</param>
    /// <param name="request">The photo, the wall and the holds built from it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was done, as counts.</returns>
    Task<HoldEnrichmentSummary> EnrichAsync(BlocwerkDbContext db, HoldEnrichmentRequest request, CancellationToken ct = default);
}
