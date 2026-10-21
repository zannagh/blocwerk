// <copyright file="EditFlushHubTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Web.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>The pre-deploy flush across circuits: tallying, a failing editor, the bounded timeout, and registration.</summary>
public class EditFlushHubTests
{

    [Fact]
    public async Task WithNoOpenEditorsTheFlushIsEmptyAndImmediate()
    {
        Assert.Equal(EditFlushReport.Empty, await NewHub().FlushAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task EveryEditorIsAskedAndTheOutcomesAreTallied()
    {
        var hub = NewHub();
        using var first = new CircuitEditFlush(hub);
        using var second = new CircuitEditFlush(hub);
        first.Register(Inline, () => Task.FromResult(EditFlushOutcome.SavedOnServer));
        first.Register(Inline, () => Task.FromResult(EditFlushOutcome.NothingToSave));
        second.Register(Inline, () => Task.FromResult(EditFlushOutcome.KeptOnDevice));

        var report = await hub.FlushAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new EditFlushReport(2, 1, 1, 1, 0, 0), report);
    }

    [Fact]
    public async Task AnEditorThatThrowsCountsAsNotSavedAndDoesNotStopTheOthers()
    {
        var hub = NewHub();
        using var circuit = new CircuitEditFlush(hub);
        circuit.Register(Inline, () => throw new InvalidOperationException("save failed"));
        circuit.Register(Inline, () => Task.FromResult(EditFlushOutcome.SavedOnServer));

        var report = await hub.FlushAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, report.NotSaved);
        Assert.Equal(1, report.SavedOnServer);
    }

    /// <summary>A circuit that never answers (its browser asleep mid-interop) cannot hold the deploy past the timeout.</summary>
    [Fact]
    public async Task AStuckCircuitIsReportedAsTimedOutWithinTheTimeout()
    {
        var hub = NewHub();
        using var stuck = new CircuitEditFlush(hub);
        using var quick = new CircuitEditFlush(hub);
        var never = new TaskCompletionSource<EditFlushOutcome>();
        stuck.Register(Inline, () => never.Task);
        quick.Register(Inline, () => Task.FromResult(EditFlushOutcome.SavedOnServer));

        var clock = Stopwatch.StartNew();
        var report = await hub.FlushAsync(TimeSpan.FromMilliseconds(200));

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Equal(2, report.Sessions);
        Assert.Equal(1, report.TimedOut);
        Assert.Equal(1, report.SavedOnServer);
    }

    [Fact]
    public async Task ClosingTheEditorOrTheCircuitTakesItOutOfTheFlush()
    {
        var hub = NewHub();
        var circuit = new CircuitEditFlush(hub);
        var registration = circuit.Register(Inline, () => Task.FromResult(EditFlushOutcome.SavedOnServer));
        Assert.Equal(1, hub.Count);

        registration.Dispose();
        Assert.Equal(0, hub.Count);

        circuit.Register(Inline, () => Task.FromResult(EditFlushOutcome.SavedOnServer));
        circuit.Dispose();
        Assert.Equal(EditFlushReport.Empty, await hub.FlushAsync(TimeSpan.FromSeconds(1)));

        // Registering on a torn-down circuit is a no-op, not a leak.
        circuit.Register(Inline, () => Task.FromResult(EditFlushOutcome.SavedOnServer));
        Assert.Equal(0, hub.Count);
    }

    /// <summary>The flush runs on the editor's own dispatcher, which is what gives it the circuit's cookie identity.</summary>
    [Fact]
    public async Task TheFlushGoesThroughTheComponentsDispatcher()
    {
        var hub = NewHub();
        using var circuit = new CircuitEditFlush(hub);
        var dispatched = 0;
        circuit.Register(
            work =>
            {
                dispatched++;
                return work();
            },
            () => Task.FromResult(EditFlushOutcome.SavedOnServer));

        await hub.FlushAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, dispatched);
    }

    // A component's InvokeAsync, minus the dispatcher.
    private static Task Inline(Func<Task> work) => work();

    private static EditFlushHub NewHub() => new(NullLogger<EditFlushHub>.Instance);
}
