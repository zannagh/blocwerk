// <copyright file="WallLookOverService.Replacement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "Different hold, and THIS hold replaced it" on an unsure match. The matcher can pair an old hold with the wrong
/// detection nearby (The Attic, October 2026: the orange hold of a boulder paired with a brown hold while a new purple hold
/// sat on the orange's spot). Plain "different hold" then leaves the detection it was paired with as the replacement, so
/// "use the new hold" would move the boulders onto the brown hold. Naming the replacement takes the pair apart the same
/// way (the detection stays a new hold of its own) and makes the named hold the one the finding asks about next, so the
/// per-boulder "use the new hold" moves the boulders that should onto it.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <summary>The refusal when the named replacement is not a hold of the finding's photo the update found.</summary>
    public const string ReplacementNotOnPhoto = "That hold is not on this photo any more, or it is the hold asked about. Reload the page.";

    /// <inheritdoc/>
    public Task DifferentHoldAsync(
        Guid wallId, Guid findingId, Guid replacementHoldId, IReadOnlyCollection<Guid> expectedBoulderIds, CancellationToken ct = default) =>
        AnswerCoreAsync(wallId, findingId, LookOverAnswer.DifferentHold, expectedBoulderIds, null, replacementHoldId, ct);

    /// <summary>
    /// The replacement must be a live hold of the finding's photo that the update found or a person placed (never a kept
    /// copy), and not the hold asked about: the same holds "it is this hold" may point at.
    /// </summary>
    private static async Task EnsureReplacementAsync(BlocwerkDbContext db, Guid wallId, LookOverFindingView view, Guid replacementHoldId, CancellationToken ct)
    {
        var panelId = view.HoldId is { } holdId
            ? await db.Holds.AsNoTracking().Where(h => h.Id == holdId).Select(h => h.WallPanelId).FirstOrDefaultAsync(ct)
            : view.PanelId;
        var targets = await PanelHoldsAsync(db, wallId, panelId, ct);
        if (replacementHoldId == view.HoldId || !targets.Any(t => t.Id == replacementHoldId && !t.Kept))
        {
            throw new UserFacingException(ReplacementNotOnPhoto);
        }
    }

    /// <summary>After the split: the named hold is the one on the spot, and whether it covers the old hold is worked out for it.</summary>
    private static async Task PointAtReplacementAsync(BlocwerkDbContext db, Entities.WallLookOverFinding finding, Guid replacementHoldId, CancellationToken ct)
    {
        var replacement = await db.Holds.AsNoTracking().FirstAsync(h => h.Id == replacementHoldId, ct);
        var old = finding.HoldId is { } holdId ? db.Holds.Local.FirstOrDefault(h => h.Id == holdId) : null;
        finding.OtherHoldId = replacement.Id;
        finding.Covered = old is not null && SameSpotFinder.Covers(replacement, old, null);
    }
}
