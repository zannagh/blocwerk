// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// Lets sync code that asks a presence probe through callbacks (the new-hold triage, the removal check) use a probe that is
/// awaited (<see cref="IAsyncHoldPresenceProbe"/>): a first run only collects its questions (answered with nulls), they are
/// then scored all at once, outside any gate slot, and a second run gets the scores replayed in the order it asks. Only for
/// code whose questions do not depend on the answers.
/// </summary>
public sealed class PresencePrefetch
{
    private readonly List<(string Pair, IReadOnlyList<PresenceQuery> Queries)> asked = [];
    private IReadOnlyList<double?>[]? answers;
    private int next;

    /// <summary>The callback to hand the sync code for the image pair <paramref name="pair"/>.</summary>
    public Func<IReadOnlyList<PresenceQuery>, IReadOnlyList<double?>> Probe(string pair) => queries =>
    {
        if (answers is null)
        {
            asked.Add((pair, queries));
            return new double?[queries.Count];
        }

        return next < answers.Length ? answers[next++] : new double?[queries.Count];
    };

    /// <summary>
    /// Scores what the first run asked, every pair at once. <paramref name="ct"/> is a budget: a pair it cancels keeps nulls,
    /// and it is never thrown.
    /// </summary>
    public async Task ScoreAsync(
        Func<string, IReadOnlyList<PresenceQuery>, CancellationToken, Task<IReadOnlyList<double?>>> score, CancellationToken ct)
    {
        answers = await Task.WhenAll(asked.Select(a => ScoreOrNullsAsync(score, a.Pair, a.Queries, ct)));
        next = 0;
    }

    private static async Task<IReadOnlyList<double?>> ScoreOrNullsAsync(
        Func<string, IReadOnlyList<PresenceQuery>, CancellationToken, Task<IReadOnlyList<double?>>> score,
        string pair,
        IReadOnlyList<PresenceQuery> queries,
        CancellationToken ct)
    {
        try
        {
            return await score(pair, queries, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new double?[queries.Count];
        }
    }
}
