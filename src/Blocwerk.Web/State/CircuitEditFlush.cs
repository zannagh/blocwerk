// <copyright file="CircuitEditFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// Per-circuit list of "persist your unsaved state now" handlers, one per open editor, run by <see cref="EditFlushHub"/>
/// right before a deploy recreates the container, whether or not the editor's lease still holds the deploy gate.
/// </summary>
/// <remarks>
/// <para>A handler runs on its component's own dispatcher (the <c>dispatch</c> passed to <see cref="Register"/> is the
/// component's <c>InvokeAsync</c>), so it sees the circuit's scoped services exactly as a click would: the current user
/// is whoever the circuit's auth cookie says, which is how a kiosk acting as a member gets the member's draft. No user
/// id is ever passed in.</para>
/// <para><b>A disconnected circuit is still flushed.</b> The editor's state lives on the server; a circuit whose browser
/// is away (a locked phone) is retained for <see cref="EditActivityPolicy.DisconnectedCircuitRetention"/> and its
/// dispatcher keeps running, so the server-side saves need no round trip. Only the browser-side extras (a copy kept in
/// localStorage, the notice) need the client, and each of those is bounded by <see cref="DeployDraftInterop"/> and runs
/// after the save.</para>
/// <para>Scoped like <see cref="CircuitEditActivity"/>; the circuit's teardown disposes it, which takes it out of the
/// hub.</para>
/// </remarks>
public sealed class CircuitEditFlush(
    EditFlushHub hub,
    IHostApplicationLifetime? lifetime = null,
    ILogger<CircuitEditFlush>? logger = null) : IDisposable
{
    private readonly object gate = new();
    private readonly List<(Func<Task<EditFlushOutcome>> Run, Func<EditFlushTarget> Describe)> handlers = new();
    private bool disposed;

    /// <summary>
    /// True while the app is shutting down. An editor's <c>Dispose</c> then comes from the circuit being torn down
    /// under it, not from the user leaving, so it must NOT forget the state it kept on the device.
    /// </summary>
    public bool Stopping => lifetime?.ApplicationStopping.IsCancellationRequested == true;

    /// <summary>What this circuit's open editors are, for the hub's timeout log.</summary>
    internal IReadOnlyList<EditFlushTarget> Targets
    {
        get
        {
            lock (gate)
            {
                return handlers.Select(h => h.Describe()).ToList();
            }
        }
    }

    /// <summary>Registers an editor's flush; dispose the result when the editor closes.</summary>
    /// <param name="dispatch">The component's <c>InvokeAsync</c>, so the flush runs on the circuit's sync context.</param>
    /// <param name="flush">Persists the editor's unsaved state and says how.</param>
    /// <param name="describe">Which editor, wall and user this is, read when a failure is logged.</param>
    public IDisposable Register(Func<Func<Task>, Task> dispatch, Func<Task<EditFlushOutcome>> flush, Func<EditFlushTarget> describe)
    {
        Func<Task<EditFlushOutcome>> onCircuit = async () =>
        {
            var outcome = EditFlushOutcome.NothingToSave;
            await dispatch(async () => outcome = await flush());
            return outcome;
        };

        var entry = (onCircuit, describe);
        lock (gate)
        {
            if (disposed)
            {
                return new CallbackDisposable(() => { });
            }

            handlers.Add(entry);
            if (handlers.Count == 1)
            {
                hub.Register(this);
            }
        }

        return new CallbackDisposable(() => Remove(entry));
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            handlers.Clear();
        }

        hub.Unregister(this);
    }

    /// <summary>Runs every handler in turn; one that throws or cannot save is logged with its editor, wall and user.</summary>
    internal async Task<IReadOnlyList<EditFlushOutcome>> RunAsync()
    {
        List<(Func<Task<EditFlushOutcome>> Run, Func<EditFlushTarget> Describe)> current;
        lock (gate)
        {
            current = new(handlers);
        }

        var outcomes = new List<EditFlushOutcome>(current.Count);
        foreach (var (run, describe) in current)
        {
            EditFlushOutcome outcome;
            Exception? failure = null;
            try
            {
                outcome = await run();
            }
            catch (Exception ex)
            {
                outcome = EditFlushOutcome.NotSaved;
                failure = ex;
            }

            if (outcome == EditFlushOutcome.NotSaved)
            {
                var target = describe();
                logger?.LogWarning(
                    failure,
                    "Pre-deploy flush could not save the {Editor} on wall {WallId} for user {UserId}",
                    target.Editor, target.WallId, target.UserId);
            }

            outcomes.Add(outcome);
        }

        return outcomes;
    }

    private void Remove((Func<Task<EditFlushOutcome>> Run, Func<EditFlushTarget> Describe) entry)
    {
        lock (gate)
        {
            handlers.Remove(entry);
            if (handlers.Count == 0)
            {
                hub.Unregister(this);
            }
        }
    }
}
