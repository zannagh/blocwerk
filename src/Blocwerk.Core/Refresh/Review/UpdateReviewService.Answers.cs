// <copyright file="UpdateReviewService.Answers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The one answer path: every answer is routed by its item's key to the write that records it, on the run's own
/// session; the summary Apply promotes is then made again once, in the background.
/// </summary>
public sealed partial class UpdateReviewService
{
    /// <summary>The most answers one call takes.</summary>
    public const int MaxAnswers = 2000;

    public async Task<UpdateReview> AnswerAsync(Guid refreshId, IReadOnlyList<ReviewAnswerRequest> answers)
    {
        if (answers.Count > MaxAnswers)
        {
            throw new UserFacingException($"Send at most {MaxAnswers} answers at once.");
        }

        var (db, refresh, userId) = await OpenAsync(refreshId);
        await using (db)
        {
            await EnsureAnswerableAsync(db, refresh);
            await WriteAllAsync(refresh, userId, answers);
            return await ReadAsync(db, await db.WallRefreshes.AsNoTracking().FirstAsync(r => r.Id == refreshId))
                ?? throw new UserFacingException("This update can no longer be changed here.");
        }
    }

    public async Task<(int Answered, UpdateReview Review)> AcceptSuggestedAsync(Guid refreshId, ReviewGroup group, Guid? panelId = null)
    {
        var review = await GetAsync(refreshId) ?? throw new UserFacingException("This update can no longer be changed here.");
        var open = review.Items
            .Where(i => i.Group == group && !i.IsAnswered && (panelId is null || i.PanelId == panelId))
            .Select(i => new ReviewAnswerRequest(i.Key, i.Suggested))
            .ToList();
        return open.Count == 0 ? (0, review) : (open.Count, await AnswerAsync(refreshId, open));
    }

    /// <summary>Writes the answers in order; the first refusal stops the rest. The summary is asked for whatever was written.</summary>
    private async Task WriteAllAsync(WallRefresh refresh, Guid userId, IReadOnlyList<ReviewAnswerRequest> answers)
    {
        var written = 0;
        try
        {
            for (var i = 0; i < answers.Count;)
            {
                // A run of answers about holds (not found, new, left out) is written in one transaction: an "accept all"
                // of a few hundred is one round, not a few hundred.
                var run = answers.Skip(i).TakeWhile(a => IsHoldKey(a.Key)).ToList();
                if (run.Count > 0)
                {
                    written += await WriteHoldRunAsync(refresh, userId, run, written);
                    i += run.Count;
                    continue;
                }

                try
                {
                    await WriteOneAsync(refresh, userId, answers[i]);
                }
                catch (UserFacingException ex)
                {
                    throw new ReviewAnswersStoppedException(written, answers[i].Key, ex);
                }

                written++;
                i++;
            }
        }
        finally
        {
            if (written > 0)
            {
                await RequestSummaryAsync(refresh.Id);
            }
        }
    }

    /// <summary>A card or a "may have moved" suggestion, through the session service as the page's other answers go.</summary>
    private Task WriteOneAsync(WallRefresh refresh, Guid userId, ReviewAnswerRequest answer)
    {
        var (prefix, id) = ParseKey(answer.Key);
        var sessionId = refresh.UpdateSessionId;
        return prefix == 'c'
            ? sessions.DecideUpdateExceptionAsync(refresh.WallId, id, ReviewAnswerSets.ToCard(answer.Answer), sessionId)
            : sessions.DecideRelocationAsync(refresh.WallId, id, ReviewAnswerSets.ToRelocation(answer.Answer), sessionId);
    }

    private static bool IsHoldKey(string key) => key is { Length: > 2 } && key[1] == '-' && key[0] is 'k' or 'n' or 'd';

    private static ReviewSubject SubjectOf(char prefix) => prefix switch
    {
        'k' => ReviewSubject.NotFound,
        'n' => ReviewSubject.NewHold,
        _ => ReviewSubject.LeftOut,
    };

    private static (char Prefix, Guid Id) ParseKey(string key)
    {
        if (key is { Length: > 2 } && key[1] == '-' && "ckndr".Contains(key[0]) && Guid.TryParse(key.AsSpan(2), out var id))
        {
            return (key[0], id);
        }

        throw new UserFacingException($"\"{key}\" is not an item of this review.");
    }

    /// <summary>Answers wait while the update is checked against the 3D model (that rewrites the decisions).</summary>
    private static async Task EnsureAnswerableAsync(BlocwerkDbContext db, WallRefresh refresh)
    {
        if (refresh.Status != WallRefreshStatus.ReadyToApply || refresh.UpdateSessionId is not { } sessionId)
        {
            throw new UserFacingException("This update can no longer be changed here.");
        }

        if (WallRefreshProcessor.IsRechecking(refresh, DateTimeOffset.UtcNow))
        {
            throw new UserFacingException(WallRefreshService.BeingChecked);
        }

        var open = await WallUpdateSessions.FindOpenAsync(db, refresh.WallId);
        if (open is null || open.Id != sessionId)
        {
            throw new UserFacingException("This update was replaced or discarded in the full review.");
        }
    }

    private async Task RequestSummaryAsync(Guid refreshId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        await db.WallRefreshes.Where(r => r.Id == refreshId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SummaryRequestedAt, now).SetProperty(r => r.UpdatedAt, now));
        queue.Enqueue(refreshId);
    }
}
