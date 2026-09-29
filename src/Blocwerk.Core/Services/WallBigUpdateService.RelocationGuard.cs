// <copyright file="WallBigUpdateService.RelocationGuard.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Keeps a "Possibly moved" suggestion only when its jump is plausible for a hold shifted on the wall
/// (see <see cref="RelocationDisplacementGuard"/>), measured on the staged centre photo from where the
/// alignment predicts the vanished hold to where the look-alike detection is.
/// </summary>
public partial class WallBigUpdateService
{
    private async Task<IReadOnlyList<RelocationPair>> GuardDisplacementAsync(
        BlocwerkDbContext db,
        BigUpdateSession session,
        IReadOnlyList<RelocationPair> pairs,
        IReadOnlyList<Hold> disappeared,
        IReadOnlyList<Hold> appeared,
        byte[]? stagedPhoto)
    {
        if (pairs.Count == 0 || stagedPhoto is null || OverlapSeedLoader.RawSize(stagedPhoto) is not { } size)
        {
            return pairs;
        }

        var (w, h) = size;
        var warp = session.CarriedWarpPositions ?? new Dictionary<Guid, HoldPositionNorm>();
        var expected = disappeared
            .Where(o => warp.ContainsKey(o.Id))
            .ToDictionary(o => o.Id, o => (warp[o.Id].X * w, warp[o.Id].Y * h));
        var actual = appeared.ToDictionary(a => a.Id, a => (a.X * w, a.Y * h));
        var twinIds = session.Carryover.Select(c => c.NewHoldId).ToList();
        var twins = await db.Holds
            .Where(x => twinIds.Contains(x.Id) && x.WallPanelId == session.CenterPanelId)
            .ToDictionaryAsync(x => x.Id, x => (x.X, x.Y));
        var anchors = session.Carryover
            .Where(c => twins.ContainsKey(c.NewHoldId))
            .Select(c => new ResidualAnchor(twins[c.NewHoldId].X * w, twins[c.NewHoldId].Y * h, c.ResidualPx))
            .ToList();

        var kept = RelocationDisplacementGuard.Filter(pairs, expected, actual, anchors, Math.Max(w, h));
        if (kept.Count < pairs.Count)
        {
            logger.LogInformation(
                "Relocation suggestions on wall {WallId}: dropped {Count} whose jump is beyond the local alignment and a plausible move",
                session.WallId, pairs.Count - kept.Count);
        }

        return kept;
    }
}
