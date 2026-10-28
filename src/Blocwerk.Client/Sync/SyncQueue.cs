// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>
/// Runs one wall's sync work one item at a time, in the order it was asked for: a stream message never overtakes the
/// refresh before it, and a resync's reload never interleaves with the events after it.
/// </summary>
internal sealed class SyncQueue
{
    private Task tail = Task.CompletedTask;

    /// <summary>Gets the work queued so far, for the tests: completes when the queue is drained.</summary>
    public Task Idle => tail;

    /// <summary>Queues <paramref name="work"/> after everything queued before it.</summary>
    /// <param name="work">The work; its failure is its own and never stops the queue.</param>
    /// <returns>A task that completes when this item ran.</returns>
    public Task Enqueue(Func<Task> work)
    {
        tail = RunAfter(tail, work);
        return tail;
    }

    private static async Task RunAfter(Task previous, Func<Task> work)
    {
        await previous;
        try
        {
            await work();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // One failed item (a malformed answer, a JS call that threw) leaves the loop to the next signal.
        }
    }
}
