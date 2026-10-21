// <copyright file="CircuitEditFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// Per-circuit list of "persist your unsaved state now" handlers, one per open editor that has unsaved state, run by
/// <see cref="EditFlushHub"/> right before a deploy recreates the container.
/// </summary>
/// <remarks>
/// <para>A handler runs on its component's own dispatcher (the <c>dispatch</c> passed to <see cref="Register"/> is the
/// component's <c>InvokeAsync</c>), so it sees the circuit's scoped services exactly as a click would: the current user
/// is whoever the circuit's auth cookie says, which is how a kiosk acting as a member gets the member's draft and an
/// anonymous tablet gets the Ghost's. No user id is ever passed in.</para>
/// <para>Scoped like <see cref="CircuitEditActivity"/>; the circuit's teardown disposes it, which takes it out of the
/// hub.</para>
/// </remarks>
public sealed class CircuitEditFlush(EditFlushHub hub, IHostApplicationLifetime? lifetime = null) : IDisposable
{
    private readonly object gate = new();
    private readonly List<Func<Task<EditFlushOutcome>>> handlers = new();
    private bool disposed;

    /// <summary>Registers an editor's flush; dispose the result when the editor closes.</summary>
    /// <param name="dispatch">The component's <c>InvokeAsync</c>, so the flush runs on the circuit's sync context.</param>
    /// <param name="flush">Persists the editor's unsaved state and says how.</param>
    public IDisposable Register(Func<Func<Task>, Task> dispatch, Func<Task<EditFlushOutcome>> flush)
    {
        Func<Task<EditFlushOutcome>> onCircuit = async () =>
        {
            var outcome = EditFlushOutcome.NothingToSave;
            await dispatch(async () => outcome = await flush());
            return outcome;
        };

        lock (gate)
        {
            if (disposed)
            {
                return new CallbackDisposable(() => { });
            }

            handlers.Add(onCircuit);
            if (handlers.Count == 1)
            {
                hub.Register(this);
            }
        }

        return new CallbackDisposable(() => Remove(onCircuit));
    }

    /// <summary>
    /// True while the app is shutting down. An editor's <c>Dispose</c> then comes from the circuit being torn down
    /// under it, not from the user leaving, so it must NOT forget the state it kept on the device.
    /// </summary>
    public bool Stopping => lifetime?.ApplicationStopping.IsCancellationRequested == true;

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            handlers.Clear();
        }

        hub.Unregister(this);
    }

    /// <summary>Runs every handler in turn; one that throws counts as <see cref="EditFlushOutcome.NotSaved"/>.</summary>
    internal async Task<IReadOnlyList<EditFlushOutcome>> RunAsync()
    {
        List<Func<Task<EditFlushOutcome>>> current;
        lock (gate)
        {
            current = new List<Func<Task<EditFlushOutcome>>>(handlers);
        }

        var outcomes = new List<EditFlushOutcome>(current.Count);
        foreach (var handler in current)
        {
            try
            {
                outcomes.Add(await handler());
            }
            catch (Exception)
            {
                outcomes.Add(EditFlushOutcome.NotSaved);
            }
        }

        return outcomes;
    }

    private void Remove(Func<Task<EditFlushOutcome>> handler)
    {
        lock (gate)
        {
            handlers.Remove(handler);
            if (handlers.Count == 0)
            {
                hub.Unregister(this);
            }
        }
    }
}
