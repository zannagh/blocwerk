// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture;

/// <summary>
/// Gives a capture's attempt back exactly once: started from the shutdown token's callback (so it runs while a stage may still
/// be busy, before a hard stop can kill the process) and awaited where the cancellation surfaces. Only a process that dies
/// (a crash, the out-of-memory killer) keeps the attempt, which is what the attempt budget is for.
/// </summary>
/// <param name="refund">The refund itself (never throws).</param>
internal sealed class CaptureAttemptRefund(Func<Task> refund)
{
    private readonly Lock gate = new();
    private Task? running;

    /// <summary>Starts the refund (once; later calls do nothing). Safe from a cancellation callback: it does not block.</summary>
    public void Start()
    {
        lock (gate)
        {
            running ??= Task.Run(refund);
        }
    }

    /// <summary>Starts the refund if it has not started and waits for it.</summary>
    /// <returns>A task.</returns>
    public Task WaitAsync()
    {
        Start();
        lock (gate)
        {
            return running!;
        }
    }
}
