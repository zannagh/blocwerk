// <copyright file="UpdateReviewScreen.Writes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh.Review;

namespace Blocwerk.Web.Components.Shared.Refresh.Review;

/// <summary>
/// The answers, optimistic: a tap marks the item answered on screen at once and turns the deck on; the answers are sent
/// in the background, in the order they were given, several at a time when they pile up. A refused answer is taken off
/// the screen again with the reason. Only Apply waits until everything is written.
/// </summary>
public partial class UpdateReviewScreen
{
    private readonly List<ReviewAnswerRequest> pending = [];
    private readonly CancellationTokenSource writes = new();
    private bool sending;

    /// <summary>
    /// Answers are still on their way to the server. A flag of its own, cleared before the last render of the loop: the
    /// loop's task is not completed yet while that render runs, so its state cannot say it.
    /// </summary>
    private bool Writing => pending.Count > 0 || sending;

    private Task AnswerAsync(ReviewItem item, ReviewAnswer answer)
    {
        var queue = Queue;
        var at = queue.ToList().FindIndex(i => i.Key == item.Key);
        Record(item.Key, answer);
        if (answer != ReviewAnswer.Undo && at >= 0)
        {
            // The deck turns to the item after the answered one (or the one before, at the end).
            currentKey = at + 1 < queue.Count ? queue[at + 1].Key : at > 0 ? queue[at - 1].Key : null;
        }
        else if (answer == ReviewAnswer.Undo)
        {
            currentKey = item.Key;
        }

        lastKey = answer == ReviewAnswer.Undo ? null : item.Key;
        return SendAsync();
    }

    private Task AnswerFromListAsync((ReviewItem Item, ReviewAnswer Answer) given) => AnswerAsync(given.Item, given.Answer);

    /// <summary>Answers every unanswered item of the group on the shown panel with its suggested answer.</summary>
    private Task AcceptAllAsync()
    {
        foreach (var item in Queue)
        {
            Record(item.Key, item.Suggested);
        }

        currentKey = null;
        return SendAsync();
    }

    private void Record(string key, ReviewAnswer answer)
    {
        overlay[key] = answer == ReviewAnswer.Undo ? null : answer;
        pending.Add(new ReviewAnswerRequest(key, answer));
        items = null;
        error = null;
    }

    private Task SendAsync()
    {
        if (!sending)
        {
            sending = true;
            _ = WriteLoopAsync();
        }

        return Task.CompletedTask;
    }

    private async Task WriteLoopAsync()
    {
        while (pending.Count > 0 && !writes.IsCancellationRequested)
        {
            var batch = pending.ToList();
            pending.Clear();
            await WriteBatchAsync(batch);
        }

        await InvokeAsync(async () =>
        {
            sending = false;
            items = null;
            StateHasChanged();
            await OnAnswered.InvokeAsync();
        });
    }

    private async Task WriteBatchAsync(List<ReviewAnswerRequest> batch)
    {
        try
        {
            var after = await Reviews.AnswerAsync(View.Id, batch);
            await InvokeAsync(() => Settle(after, batch, new HashSet<string>()));
        }
        catch (ReviewAnswersStoppedException ex)
        {
            var lost = batch.Skip(ex.Answered).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
            var after = await Reviews.GetAsync(View.Id);
            await InvokeAsync(() => Settle(after, batch, lost, $"{PlainCopy.Plural(lost.Count, "answer")} not saved: {ex.Message}"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            var after = await Reviews.GetAsync(View.Id);
            await InvokeAsync(() => Settle(after, batch, batch.Select(a => a.Key).ToHashSet(StringComparer.Ordinal), ex.Message));
        }
    }

    /// <summary>The server's review is the truth again for every key of the batch nobody answered anew meanwhile.</summary>
    private void Settle(Core.Refresh.Review.UpdateReview? after, List<ReviewAnswerRequest> batch, IReadOnlySet<string> lost, string? message = null)
    {
        review = after ?? review;
        foreach (var key in batch.Select(a => a.Key).Distinct(StringComparer.Ordinal))
        {
            if (lost.Contains(key) || pending.All(p => p.Key != key))
            {
                overlay.Remove(key);
            }
        }

        error = message ?? error;
        items = null;
        StateHasChanged();
    }
}
