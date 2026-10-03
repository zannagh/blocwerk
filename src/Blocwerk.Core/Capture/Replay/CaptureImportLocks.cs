// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// One lock per open import: its file uploads, commit and abort run one at a time, so a retried or parallel request of
/// the rollout script never unlinks another's partial upload or moves files a commit is moving. In process (the app is a
/// single instance, like <see cref="WallCaptureQueue"/>).
/// </summary>
internal static class CaptureImportLocks
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    /// <summary>Runs <paramref name="action"/> while holding the import's lock.</summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="importId">The import.</param>
    /// <param name="action">The work.</param>
    /// <param name="ct">Cancellation (of the wait).</param>
    /// <returns>What <paramref name="action"/> returned.</returns>
    public static async Task<T> RunAsync<T>(Guid importId, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(importId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }
}
