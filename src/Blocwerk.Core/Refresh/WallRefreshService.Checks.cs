// <copyright file="WallRefreshService.Checks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The confirm screen's cards: listing them, their pictures, and the user's answers. An answer is written to the update
/// session as the user (the decision the full review would record), then the summary is made again on the worker
/// (<see cref="WallRefresh.SummaryRequestedAt"/>) so Apply promotes what the screen shows.
/// </summary>
public sealed partial class WallRefreshService
{
    /// <summary>The most answers one call takes.</summary>
    public const int MaxCheckAnswers = 500;

    /// <summary>The card kinds whose "Remove" removes the OLD hold: only taken for a hold on the centre panel.</summary>
    private static readonly HashSet<UpdateExceptionKind> RemovalKinds =
        [UpdateExceptionKind.ReplacedByOther, UpdateExceptionKind.PossiblyRemoved];

    public async Task<IReadOnlyList<RefreshCheck>> GetChecksAsync(Guid refreshId)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            if (refresh.Status != WallRefreshStatus.ReadyToApply || refresh.UpdateSessionId is not { } sessionId)
            {
                return [];
            }

            var cards = await WallUpdateSessionService.ReadExceptionsAsync(db, sessionId);
            var oldIds = cards.Select(c => c.OldHoldId).OfType<Guid>().ToList();
            var holds = await db.Holds.AsNoTracking().Where(h => oldIds.Contains(h.Id))
                .Select(h => new { h.Id, h.Name, h.Color, OnCentre = h.WallPanel == null || (h.WallPanel.Col == 0 && h.WallPanel.Row == 0) })
                .ToDictionaryAsync(h => h.Id);
            var boulders = (await db.BoulderHolds.AsNoTracking()
                    .Where(bh => oldIds.Contains(bh.HoldId) && !bh.Boulder.IsHistoric)
                    .Select(bh => new { bh.HoldId, bh.BoulderId })
                    .ToListAsync())
                .GroupBy(bh => bh.HoldId)
                .ToDictionary(g => g.Key, g => g.Select(bh => bh.BoulderId).Distinct().Count());
            return cards.Select(c =>
            {
                var hold = c.OldHoldId is { } id ? holds.GetValueOrDefault(id) : null;
                var removable = !RemovalKinds.Contains(c.Kind) || hold is not { OnCentre: false };

                // A removal stored before it was refused off the centre is reset to "carried" at the promote: the card
                // says what will happen (open, asked again in the look-over), not "Removed".
                var status = !removable && c.Status == UpdateExceptionStatus.Removed ? UpdateExceptionStatus.Pending : c.Status;
                return new RefreshCheck(
                    c.Id, c.Kind, status, hold is not null, c.HasTexture,
                    c.OldHoldId is { } o ? boulders.GetValueOrDefault(o) : 0, hold?.Name ?? hold?.Color, c.Confidence,
                    removable);
            }).ToList();
        }
    }

    public async Task<byte[]?> GetCheckCropAsync(Guid refreshId, Guid checkId, CheckCropView view, CancellationToken ct)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            return refresh.UpdateSessionId is { } sessionId
                ? await RefreshCheckCrops.GetAsync(dbContextFactory, files, sessionId, checkId, view, ct)
                : null;
        }
    }

    public Task DecideCheckAsync(Guid refreshId, Guid checkId, UpdateExceptionAnswer answer) =>
        DecideChecksAsync(refreshId, [new RefreshCheckAnswer(checkId, answer)]);

    public async Task<int> DecideChecksAsync(Guid refreshId, IReadOnlyList<RefreshCheckAnswer> answers)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            EnsureAnswerable(refresh);
        }

        if (answers.Count == 0)
        {
            return 0;
        }

        if (answers.Count > MaxCheckAnswers)
        {
            throw new UserFacingException($"Send at most {MaxCheckAnswers} answers at once.");
        }

        var user = await currentUserService.GetCurrentUserAsync();
        await using (var scope = await actorFactory.CreateAsync(user.Id, CancellationToken.None))
        {
            var open = await scope.Actors.Sessions.GetOpenSessionAsync(refresh.WallId);
            if (open is null || open.Id != refresh.UpdateSessionId)
            {
                throw new UserFacingException("This update was replaced or discarded in the full review.");
            }

            await WriteAnswersAsync(refreshId, refresh, scope.Actors.Sessions, answers);
        }

        return answers.Count;
    }

    /// <summary>
    /// Writes the answers in order, each naming the run's session (one landing after a takeover is refused, never written
    /// there). The first refusal stops the rest; the summary is made again for whatever was written, even then.
    /// </summary>
    private async Task WriteAnswersAsync(
        Guid refreshId, WallRefresh refresh, IWallUpdateSessionService sessions, IReadOnlyList<RefreshCheckAnswer> answers)
    {
        var written = 0;
        try
        {
            foreach (var answer in answers)
            {
                try
                {
                    await sessions.DecideUpdateExceptionAsync(refresh.WallId, answer.CheckId, answer.Answer, refresh.UpdateSessionId);
                }
                catch (UserFacingException ex)
                {
                    throw new RefreshCheckAnswersStoppedException(written, answer.CheckId, ex);
                }

                written++;
            }
        }
        finally
        {
            if (written > 0)
            {
                await RequestSummaryAsync(refreshId);
                queue.Enqueue(refreshId);
            }
        }
    }

    private static void EnsureAnswerable(WallRefresh refresh)
    {
        if (refresh.Status != WallRefreshStatus.ReadyToApply)
        {
            throw new UserFacingException("This update can no longer be changed here.");
        }

        if (WallRefreshProcessor.IsRechecking(refresh, DateTimeOffset.UtcNow))
        {
            throw new UserFacingException("The update is being checked against the new 3D model. Try again in a moment.");
        }
    }

    private async Task RequestSummaryAsync(Guid refreshId)
    {
        var (db, row) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            row.SummaryRequestedAt = DateTimeOffset.UtcNow;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
    }
}
