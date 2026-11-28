// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>The update path of a grade proposal, for a first proposal that lost the race to another one.</summary>
public partial class BoulderService
{
    /// <summary>Updates the boulder's one open proposal to <paramref name="proposedGrade"/> by <paramref name="user"/>.</summary>
    private async Task<GradeProposal> UpdateOpenProposalAsync(Guid boulderId, string proposedGrade, User user)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        var open = await db.GradeProposals.FirstOrDefaultAsync(gp => gp.BoulderId == boulderId && !gp.IsResolved)
                   ?? throw new InvalidOperationException("Proposal not found");
        open.ProposedGrade = proposedGrade;
        open.ProposedByUserId = user.Id;
        open.CreatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        _logger.LogInformation("Grade proposal {ProposalId} updated for boulder {BoulderId} by {UserId} after a concurrent create", open.Id, boulderId, user.Id);
        open.ProposedBy = user;
        return open;
    }
}
