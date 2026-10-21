// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// A stand-in for a circuit's dispatcher: work runs one piece at a time, interleaving only at awaits, as under Blazor's
/// renderer. The hub's hand-off never waits for it.
/// </summary>
internal sealed class ExclusiveDispatcher
{
    private readonly TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;

    public Task Run(Func<Task> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, scheduler).Unwrap();

    public Task<T> Run<T>(Func<Task<T>> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, scheduler).Unwrap();
}
