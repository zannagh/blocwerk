// <copyright file="UpdateReviewScreen.Writes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh.Review;
using Blocwerk.Core.Services;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared.Refresh.Review;

/// <summary>
/// The answers, optimistic but never claimed saved early: a tap turns the deck on at once and the item reads "saving…"
/// until the server has it; the answers go out in the background, in order, several at a time when they pile up. An
/// answer the server refuses is taken off the screen with the reason. One that could not be sent at all (the server or
/// the network failed) is kept as "not saved" with Retry. Apply waits until nothing is in flight, and closing the tab
/// meanwhile asks first.
/// </summary>
public partial class UpdateReviewScreen
{
    /// <summary>What an answer reads as until the server confirms it.</summary>
    public const string Saving = "saving…";

    private readonly PendingAnswers pending = new();
    private readonly CancellationTokenSource writes = new();
    private bool sending;
    private bool accepting;
    private bool guarded;

    /// <summary>Answers are on their way to the server, or could not be saved: Apply waits.</summary>
    private bool Writing => pending.Waiting > 0 || sending || accepting || pending.Unsaved > 0;

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

    /// <summary>
    /// "All as suggested" for the group on the shown panel, answered by the server from its own state: an item someone
    /// answered meanwhile (another tab, another admin) keeps that answer.
    /// </summary>
    private async Task AcceptAllAsync()
    {
        if (group is not { } g || accepting)
        {
            return;
        }

        accepting = true;
        try
        {
            var (_, after) = await Reviews.AcceptSuggestedAsync(View.Id, g, panel);
            (review, items, currentKey, error) = (after, null, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex is UserFacingException ? ex.Message : "The answers could not be saved. Try again.";
        }
        finally
        {
            accepting = false;
        }

        await OnAnswered.InvokeAsync();
    }

    /// <summary>Sends the answers that could not be saved again.</summary>
    private Task RetryAsync()
    {
        foreach (var answer in pending.TakeUnsaved())
        {
            Record(answer.Key, answer.Answer);
        }

        return SendAsync();
    }

    private void Record(string key, ReviewAnswer answer)
    {
        // The latest answer per item wins: an unsaved older one is not sent again after it.
        overlay[key] = answer == ReviewAnswer.Undo ? null : answer;
        pending.Add(new ReviewAnswerRequest(key, answer));
        items = null;
        error = null;
    }

    private async Task SendAsync()
    {
        if (!sending)
        {
            sending = true;
            await GuardTabAsync(true);
            _ = WriteLoopAsync();
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (pending.Waiting > 0 && !writes.IsCancellationRequested)
            {
                await WriteBatchAsync(pending.TakeBatch());
            }
        }
        finally
        {
            await InvokeAsync(async () =>
            {
                sending = false;
                items = null;
                StateHasChanged();
                await GuardTabAsync(pending.Unsaved > 0);
                await OnAnswered.InvokeAsync();
            });
        }
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
            await InvokeAsync(async () => Settle(await ReadOrKeepAsync(), batch, lost, $"{PlainCopy.Plural(lost.Count, "answer")} not saved: {ex.Message}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !writes.IsCancellationRequested)
        {
            // Not refused, just not sent: kept for Retry, never shown as saved.
            await InvokeAsync(async () =>
            {
                pending.Failed(batch);
                Settle(await ReadOrKeepAsync(), batch, batch.Select(a => a.Key).ToHashSet(StringComparer.Ordinal));
            });
        }
    }

    private async Task<Core.Refresh.Review.UpdateReview?> ReadOrKeepAsync()
    {
        try
        {
            return await Reviews.GetAsync(View.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The server's review is the truth again for every key of the batch nobody answered anew meanwhile.</summary>
    private void Settle(Core.Refresh.Review.UpdateReview? after, List<ReviewAnswerRequest> batch, IReadOnlySet<string> lost, string? message = null)
    {
        review = after ?? review;
        foreach (var key in batch.Select(a => a.Key).Distinct(StringComparer.Ordinal).Where(k => lost.Contains(k) || !pending.IsWaiting(k)))
        {
            overlay.Remove(key);
        }

        error = message ?? error;
        items = null;
        StateHasChanged();
    }

    /// <summary>Asks before the tab closes while answers are in flight or unsaved (best effort: a gone circuit has no JS).</summary>
    private async Task GuardTabAsync(bool on)
    {
        if (on == guarded)
        {
            return;
        }

        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/unsaved-guard.js");
            await module.InvokeVoidAsync("guard", on);
            guarded = on;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            // Nothing to guard any more.
        }
    }
}
