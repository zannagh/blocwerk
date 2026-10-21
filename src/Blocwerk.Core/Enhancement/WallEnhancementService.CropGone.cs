// <copyright file="WallEnhancementService.CropGone.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// The review picture of a hold that is gone since the run (an accepted duplicate merged it away): an answered card
/// still shows what was decided, drawn from the hold's last row in the change journal instead of a 404.
/// </summary>
public sealed partial class WallEnhancementService
{
    /// <summary>The hold as it was when it was deleted, from its newest journalled delete, or null when none is recorded.</summary>
    private async Task<Hold?> DeletedHoldAsync(Guid wallId, Guid holdId, CancellationToken ct)
    {
        await using var scratch = await dbContextFactory.CreateDbContextAsync(ct);
        var needle = holdId.ToString();
        var deletes = await scratch.ChangeJournalEntries.AsNoTracking()
            .Where(e => e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Delete && e.BeforeJson != null && e.KeyJson.Contains(needle))
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);
        var last = deletes.FirstOrDefault(e => WallEnhancementJournal.HoldIdOf(e.KeyJson) == holdId);
        var entityType = scratch.Model.FindEntityType(typeof(Hold));
        if (last is null || entityType is null)
        {
            return null;
        }

        var hold = new Hold();
        ChangeJournalEntityAccessor.ApplyValues(scratch.Entry(hold), ChangeJournalValueWriter.DeserializeValues(entityType, last.BeforeJson!, _ => null));
        return hold.WallId == wallId ? hold : null;
    }
}
