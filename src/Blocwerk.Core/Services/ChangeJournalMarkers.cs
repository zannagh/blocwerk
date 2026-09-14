using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Shared helpers for the export/replay fail-fast gates: the EF migration id (schema parity) and a
/// coarse per-scope base marker (generation + a hash of the scope's current live rows).
/// </summary>
internal static class ChangeJournalMarkers
{
    /// <summary>
    /// The last EF migration applied to this database, or — when there is no migrations history
    /// (e.g. a test schema created via EnsureCreated) — the latest migration defined in the assembly.
    /// Empty only when neither is available.
    /// </summary>
    public static async Task<string> MigrationIdAsync(DbContext context, CancellationToken cancellationToken)
    {
        try
        {
            var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
            var last = applied.LastOrDefault();
            if (!string.IsNullOrEmpty(last))
            {
                return last;
            }
        }
        catch
        {
            // No migrations history table (EnsureCreated) — fall back to the defined-latest below.
        }

        return context.Database.GetMigrations().LastOrDefault() ?? string.Empty;
    }

    /// <summary>Computes the base marker for one scoped aggregate from its current live rows.</summary>
    public static async Task<ChangeJournalPackageBaseMarker> ComputeAsync(
        BlocwerkDbContext context, ChangeJournalScopeKind scopeKind, Guid? scopeId, CancellationToken cancellationToken)
    {
        var marker = new ChangeJournalPackageBaseMarker { ScopeKind = scopeKind, ScopeId = scopeId };
        if (scopeKind != ChangeJournalScopeKind.Wall || scopeId is not { } wallId)
        {
            marker.LiveHash = string.Empty;
            return marker;
        }

        var generation = await context.Walls
            .Where(w => w.Id == wallId)
            .Select(w => (int?)w.CurrentGeneration)
            .FirstOrDefaultAsync(cancellationToken);

        marker.Generation = generation ?? -1;
        if (generation is null)
        {
            marker.LiveHash = string.Empty; // wall absent — replay treats a missing scope as a hard divergence.
            return marker;
        }

        var liveHolds = await context.Holds
            .Where(h => h.WallId == wallId && h.Generation == generation)
            .OrderBy(h => h.Id)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken);

        var liveBoulders = await context.Boulders
            .Where(b => b.WallId == wallId && !b.IsArchived && b.Generation == generation)
            .OrderBy(b => b.Id)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        marker.LiveHash = Hash(generation.Value, liveHolds, liveBoulders);
        return marker;
    }

    private static string Hash(int generation, IEnumerable<Guid> holds, IEnumerable<Guid> boulders)
    {
        var builder = new StringBuilder();
        builder.Append(generation).Append("|H:");
        builder.AppendJoin(',', holds);
        builder.Append("|B:");
        builder.AppendJoin(',', boulders);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
