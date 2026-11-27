// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// One lock per open import: its begin, file uploads, commit and abort run one at a time, so a retried or parallel
/// request of the rollout script never unlinks another's partial upload or moves files a commit is moving. In process
/// (the app is a single instance, like <see cref="WallCaptureQueue"/>). A lock exists only while a request holds or waits
/// for it, so committed and abandoned imports leave nothing behind.
/// </summary>
internal static class CaptureImportLocks
{
    private static readonly KeyedAsyncLocks<Guid> Locks = new();

    /// <summary>Whether the import has a lock right now (for tests).</summary>
    /// <param name="importId">The import.</param>
    /// <returns>True while a request holds or waits for it.</returns>
    public static bool Has(Guid importId) => Locks.Any(id => id == importId);

    /// <summary>Runs <paramref name="action"/> while holding the import's lock.</summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="importId">The import.</param>
    /// <param name="action">The work.</param>
    /// <param name="ct">Cancellation (of the wait).</param>
    /// <returns>What <paramref name="action"/> returned.</returns>
    public static Task<T> RunAsync<T>(Guid importId, Func<Task<T>> action, CancellationToken ct) =>
        Locks.RunAsync(importId, action, ct);
}
