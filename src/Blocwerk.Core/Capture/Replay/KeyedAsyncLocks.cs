// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// One async lock per key, in process. A key's lock exists only while a caller holds or waits for it, so finished work
/// leaves nothing behind. <see cref="CaptureImportLocks"/> keys it by import (or scan) id; the scan upload also keys one
/// by scan and file name, so different files of one scan stream in parallel.
/// </summary>
/// <typeparam name="TKey">The key.</typeparam>
internal sealed class KeyedAsyncLocks<TKey>
    where TKey : notnull
{
    private readonly Dictionary<TKey, CaptureImportLockEntry> locks = [];
    private readonly Lock sync = new();

    /// <summary>Whether any key matching <paramref name="match"/> is held or waited for right now.</summary>
    /// <param name="match">Which keys count.</param>
    /// <returns>True while a caller holds or waits for such a key.</returns>
    public bool Any(Func<TKey, bool> match)
    {
        lock (sync)
        {
            return locks.Keys.Any(match);
        }
    }

    /// <summary>Runs <paramref name="action"/> while holding <paramref name="key"/>'s lock.</summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="action">The work.</param>
    /// <param name="ct">Cancellation (of the wait).</param>
    /// <returns>What <paramref name="action"/> returned.</returns>
    public async Task<T> RunAsync<T>(TKey key, Func<Task<T>> action, CancellationToken ct)
    {
        CaptureImportLockEntry entry;
        lock (sync)
        {
            if (!locks.TryGetValue(key, out entry!))
            {
                entry = new CaptureImportLockEntry();
                locks[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct);
            try
            {
                return await action();
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        finally
        {
            lock (sync)
            {
                if (--entry.Users == 0)
                {
                    locks.Remove(key);
                    entry.Gate.Dispose();
                }
            }
        }
    }
}
