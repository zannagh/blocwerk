// <copyright file="PendingAnswers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Refresh.Review;

namespace Blocwerk.Web.Components.Shared.Refresh.Review;

/// <summary>
/// The review page's answers that the server does not have yet: those waiting to be sent, and those whose sending failed
/// (kept for Retry). The latest answer per item always wins: an answer given after a failure replaces the unsaved one,
/// so a retry never sends an older answer after a newer one.
/// </summary>
public sealed class PendingAnswers
{
    private readonly List<ReviewAnswerRequest> waiting = [];
    private readonly List<ReviewAnswerRequest> unsaved = [];

    /// <summary>Gets the number of answers waiting to be sent.</summary>
    public int Waiting => waiting.Count;

    /// <summary>Gets the number of answers whose sending failed.</summary>
    public int Unsaved => unsaved.Count;

    /// <summary>Whether an answer about <paramref name="key"/> is still waiting to be sent.</summary>
    public bool IsWaiting(string key) => waiting.Any(a => a.Key == key);

    /// <summary>A new answer: it replaces an unsaved one about the same item and waits to be sent.</summary>
    public void Add(ReviewAnswerRequest answer)
    {
        unsaved.RemoveAll(u => u.Key == answer.Key);
        waiting.Add(answer);
    }

    /// <summary>Everything waiting, in order, to send as one batch.</summary>
    public List<ReviewAnswerRequest> TakeBatch()
    {
        var batch = waiting.ToList();
        waiting.Clear();
        return batch;
    }

    /// <summary>A batch that could not be sent: kept for Retry, unless the item was answered again meanwhile.</summary>
    public void Failed(IEnumerable<ReviewAnswerRequest> batch)
    {
        foreach (var answer in batch.GroupBy(a => a.Key, StringComparer.Ordinal).Select(g => g.Last()).Where(a => !IsWaiting(a.Key)))
        {
            unsaved.RemoveAll(u => u.Key == answer.Key);
            unsaved.Add(answer);
        }
    }

    /// <summary>The unsaved answers, one per item (the latest), taken back for sending again.</summary>
    public List<ReviewAnswerRequest> TakeUnsaved()
    {
        var again = unsaved.ToList();
        unsaved.Clear();
        return again;
    }
}
