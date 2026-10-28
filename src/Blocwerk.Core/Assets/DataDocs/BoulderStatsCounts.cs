// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// The wall-wide counts of the <c>boulders</c> document: climbers who sent or flashed a boulder, and its ratings. Counted
/// in the database and averaged here, so both providers give the same bytes.
/// </summary>
internal static class BoulderStatsCounts
{
    /// <summary>Gets the counts of a boulder nobody has ticked or rated.</summary>
    public static BouldersDocumentStats None { get; } = new(0, 0, null);

    public static async Task<Dictionary<Guid, BouldersDocumentStats>> LoadAsync(BlocwerkDbContext db, List<Guid> boulderIds, CancellationToken ct)
    {
        var ascents = await db.Attempts.AsNoTracking()
            .Where(a => boulderIds.Contains(a.BoulderId) && (a.Type == AttemptType.Send || a.Type == AttemptType.Flash))
            .Select(a => new { a.BoulderId, a.UserId })
            .Distinct()
            .GroupBy(a => a.BoulderId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var ratings = await db.BoulderRatings.AsNoTracking()
            .Where(r => boulderIds.Contains(r.BoulderId))
            .GroupBy(r => r.BoulderId)
            .Select(g => new { g.Key, Count = g.Count(), Sum = g.Sum(r => r.Stars) })
            .ToDictionaryAsync(g => g.Key, g => (g.Count, g.Sum), ct);

        return boulderIds.ToDictionary(id => id, id =>
        {
            var (count, sum) = ratings.GetValueOrDefault(id);
            return new BouldersDocumentStats(
                ascents.GetValueOrDefault(id),
                count,
                count == 0 ? null : Math.Round((double)sum / count, 2, MidpointRounding.AwayFromZero));
        });
    }
}
