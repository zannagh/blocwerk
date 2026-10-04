// <copyright file="HoldSearchJobs.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Services;

/// <summary>Where a hold search is: photos searched so far out of all.</summary>
/// <param name="Done">Photos searched.</param>
/// <param name="Total">Photos to search.</param>
public sealed record HoldSearchProgress(int Done, int Total);

/// <summary>One wall's latest hold search as the UI and the progress API show it.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="State">One of <see cref="JobStates"/>: queued, running, succeeded, failed or cancelled.</param>
/// <param name="Done">Photos searched so far.</param>
/// <param name="Total">Photos to search (0 until known).</param>
/// <param name="StartedAt">When it was requested.</param>
/// <param name="UpdatedAt">When it last reported.</param>
/// <param name="EndedAt">When it ended, or null.</param>
/// <param name="Result">The outcome of a search that succeeded.</param>
/// <param name="Error">A plain-words reason for a search that failed.</param>
public sealed record HoldSearchStatus(
    Guid WallId,
    string State,
    int Done,
    int Total,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? EndedAt = null,
    HoldProposalRunResult? Result = null,
    string? Error = null)
{
    /// <summary>Whether the search is still to end.</summary>
    public bool Active => JobStates.IsActive(State);
}

/// <summary>
/// Runs "Find holds from all photos" in the background, at most one search per wall at a time, and remembers each
/// wall's latest search (running or ended) for the page and the job list. The proposals themselves are stored by
/// the search, so only this status is in memory: a restart forgets it but keeps every proposal.
/// </summary>
public sealed class HoldSearchJobs(TimeProvider? clock = null, ILogger<HoldSearchJobs>? logger = null) : IDisposable
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ILogger log = (ILogger?)logger ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<Guid, HoldSearchStatus> statuses = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();

    /// <summary>The search to run: report progress, honour the token, return the outcome or throw.</summary>
    /// <param name="progress">Progress sink.</param>
    /// <param name="ct">Cancelled when the server shuts down.</param>
    /// <returns>The outcome.</returns>
    public delegate Task<HoldProposalRunResult> Work(IProgress<HoldSearchProgress> progress, CancellationToken ct);

    /// <summary>The latest search of a wall, or null when none ran since the server started.</summary>
    /// <param name="wallId">The wall.</param>
    /// <returns>The status.</returns>
    public HoldSearchStatus? Get(Guid wallId) => statuses.GetValueOrDefault(wallId);

    /// <summary>The latest search of every wall.</summary>
    /// <returns>The statuses.</returns>
    public IReadOnlyList<HoldSearchStatus> All() => [.. statuses.Values];

    /// <summary>Starts a search in the background unless the wall already has one running or queued.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="work">The search.</param>
    /// <param name="status">The new status, or the running search's.</param>
    /// <returns>False when the wall already has an active search (nothing was started).</returns>
    public bool TryStart(Guid wallId, Work work, out HoldSearchStatus status)
    {
        lock (gate)
        {
            if (statuses.TryGetValue(wallId, out var existing) && existing.Active)
            {
                status = existing;
                return false;
            }

            var now = time.GetUtcNow();
            status = new HoldSearchStatus(wallId, JobStates.Queued, 0, 0, now, now);
            statuses[wallId] = status;
        }

        _ = Task.Run(() => RunAsync(wallId, work));
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!stopping.IsCancellationRequested)
        {
            stopping.Cancel();
        }
    }

    private async Task RunAsync(Guid wallId, Work work)
    {
        try
        {
            var result = await work(new HoldSearchReporter(p => Update(wallId, s => s with { State = JobStates.Running, Done = p.Done, Total = p.Total })), stopping.Token);
            Finish(wallId, s => s with { State = JobStates.Succeeded, Done = s.Total, Result = result });
        }
        catch (OperationCanceledException)
        {
            Finish(wallId, s => s with { State = JobStates.Cancelled, Error = "The search was stopped because the server restarted." });
        }
        catch (UserFacingException ex)
        {
            Finish(wallId, s => s with { State = JobStates.Failed, Error = ex.Message });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "The hold search of wall {WallId} failed", wallId);
            Finish(wallId, s => s with { State = JobStates.Failed, Error = "The search failed. Try again in a few minutes." });
        }
    }

    private void Finish(Guid wallId, Func<HoldSearchStatus, HoldSearchStatus> change) =>
        Update(wallId, s => change(s) with { EndedAt = time.GetUtcNow() });

    private void Update(Guid wallId, Func<HoldSearchStatus, HoldSearchStatus> change)
    {
        lock (gate)
        {
            if (statuses.TryGetValue(wallId, out var current))
            {
                statuses[wallId] = change(current) with { UpdatedAt = time.GetUtcNow() };
            }
        }
    }
}

// Progress<T> would post to a captured context; the status must change at once, on the reporting thread.
internal sealed class HoldSearchReporter(Action<HoldSearchProgress> report) : IProgress<HoldSearchProgress>
{
    public void Report(HoldSearchProgress value) => report(value);
}
