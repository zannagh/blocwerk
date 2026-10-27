// <copyright file="UpdateReviewService.MoveAnswers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The answers about a carried hold the measurement says may have moved. "It moved" is the very verdict a person records
/// by hand (changed, paired with the same detection, confirmed), so the promote marks its boulders or takes it off them
/// as for any confirmed move. "Same hold" is recorded as the review's answer; the carry stays the match it was (a "changed"
/// verdict goes back to it), which leaves the boulders alone. Undo takes back only what "it moved" wrote.
/// </summary>
public sealed partial class UpdateReviewService
{
    private static async Task AnswerMoveAsync(BlocwerkDbContext db, Guid sessionId, Guid oldHoldId, ReviewAnswer answer, Guid userId, DateTimeOffset now)
    {
        var row = await db.WallUpdateHoldDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.Carry && d.HoldId == oldHoldId);
        if (row is not { PairedHoldId: { } twin } || row.CarryKind == CarryKind.Removed)
        {
            throw new UserFacingException("This old hold is no longer carried onto a detection; answer it where it is listed now.");
        }

        switch (answer)
        {
            case ReviewAnswer.Moved:
                CarryConfirmationPolicy.Apply(row, CarryKind.Changed, twin, confirmed: true, userId, now);
                break;
            case ReviewAnswer.SameHold or ReviewAnswer.Undo when row.CarryKind == CarryKind.Changed:
                CarryConfirmationPolicy.Apply(row, CarryKind.Carried, twin, confirmed: false, userId, now);
                break;
            case ReviewAnswer.SameHold or ReviewAnswer.Undo:
                break;
            default:
                throw new UserFacingException("A hold that may have moved is answered \"same hold\" or \"it moved\".");
        }

        row.UpdatedAt = now;
    }
}
