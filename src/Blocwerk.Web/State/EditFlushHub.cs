// <copyright file="EditFlushHub.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace Blocwerk.Web.State;

/// <summary>
/// App-wide set of circuits with an editor that can persist its unsaved state (<see cref="CircuitEditFlush"/>), and
/// the pre-deploy flush across all of them. Singleton, in memory, like <see cref="EditActivityRegistry"/>.
/// </summary>
/// <remarks>
/// The flush runs every circuit at once and waits at most the given timeout: a circuit whose browser is asleep can
/// still save on the server, but anything that needs the browser (keeping state on the device, the notice) waits on
/// interop with its own short timeout, and one stuck circuit must not hold the deploy for the rest. A circuit that has
/// not answered by then is counted as timed out; its work carries on in the background until the container stops.
/// </remarks>
public sealed class EditFlushHub(ILogger<EditFlushHub> logger)
{
    private readonly ConcurrentDictionary<CircuitEditFlush, byte> circuits = new();

    /// <summary>Number of circuits with at least one flushable editor.</summary>
    public int Count => circuits.Count;

    /// <summary>Asks every open editor to persist its unsaved state, waiting at most <paramref name="timeout"/>.</summary>
    public async Task<EditFlushReport> FlushAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var sessions = circuits.Keys.ToList();
        if (sessions.Count == 0)
        {
            return EditFlushReport.Empty;
        }

        var runs = sessions.Select(s => Task.Run(s.RunAsync, CancellationToken.None)).ToList();
        try
        {
            await Task.WhenAny(Task.WhenAll(runs), Task.Delay(timeout, ct));
        }
        catch (OperationCanceledException)
        {
            // The caller went away; report what finished.
        }

        var report = Tally(runs);
        logger.LogInformation(
            "Pre-deploy flush over {Sessions} circuit(s): {Saved} saved, {Kept} kept on device, {Nothing} with nothing to save, {NotSaved} not saved, {TimedOut} timed out",
            report.Sessions, report.SavedOnServer, report.KeptOnDevice, report.NothingToSave, report.NotSaved, report.TimedOut);
        return report;
    }

    internal void Register(CircuitEditFlush circuit) => circuits.TryAdd(circuit, 0);

    internal void Unregister(CircuitEditFlush circuit) => circuits.TryRemove(circuit, out _);

    private static EditFlushReport Tally(IReadOnlyList<Task<IReadOnlyList<EditFlushOutcome>>> runs)
    {
        var outcomes = runs.Where(r => r.IsCompletedSuccessfully).SelectMany(r => r.Result).ToList();
        return new EditFlushReport(
            runs.Count,
            outcomes.Count(o => o == EditFlushOutcome.SavedOnServer),
            outcomes.Count(o => o == EditFlushOutcome.KeptOnDevice),
            outcomes.Count(o => o == EditFlushOutcome.NothingToSave),
            outcomes.Count(o => o == EditFlushOutcome.NotSaved) + runs.Count(r => r.IsFaulted),
            runs.Count(r => !r.IsCompleted));
    }
}
