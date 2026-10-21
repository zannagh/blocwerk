// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Services;

/// <summary>
/// The render discipline of <see cref="FileSystemImageVariantCache"/>: one flight per rendition (concurrent misses of
/// the same <c>(source, width)</c> share one load and one render), and at most <see cref="RenderConcurrency"/> renders
/// in the whole process at once, requests and the warmer together. After a deploy a page full of cold renditions then
/// queues instead of decoding a dozen camera originals in parallel.
/// </summary>
public sealed class RenditionFlights
{
    /// <summary>Renders at once, process-wide: the warmer's own concurrency (<c>ImageVariantWarmer.Concurrency</c>).</summary>
    public const int RenderConcurrency = 2;

    private readonly SemaphoreSlim renders = new(RenderConcurrency, RenderConcurrency);
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageVariant?>>> flights = new();

    /// <summary>
    /// The result of <paramref name="miss"/> for <paramref name="key"/>, run once for every caller that asks while it
    /// runs. A caller that gives up only stops waiting. When the flight's owner gave up (its loader was cancelled), a
    /// caller still waiting runs the miss once itself.
    /// </summary>
    public async Task<ImageVariant?> RunAsync(string key, Func<Task<ImageVariant?>> miss, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            Lazy<Task<ImageVariant?>>? mine = null;
            mine = new Lazy<Task<ImageVariant?>>(() => FlyAsync(key, miss, mine!));
            var flight = flights.GetOrAdd(key, mine);
            try
            {
                return await flight.Value.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt == 0)
            {
                // The flight's owner gave up mid-load; this caller still wants it.
            }
        }
    }

    /// <summary>Renders under the process-wide limit.</summary>
    /// <typeparam name="T">The rendition.</typeparam>
    public async Task<T> RenderAsync<T>(Func<T> render, CancellationToken ct)
    {
        await renders.WaitAsync(ct);
        try
        {
            return render();
        }
        finally
        {
            renders.Release();
        }
    }

    private async Task<ImageVariant?> FlyAsync(string key, Func<Task<ImageVariant?>> miss, Lazy<Task<ImageVariant?>> self)
    {
        try
        {
            return await miss();
        }
        finally
        {
            flights.TryRemove(KeyValuePair.Create(key, self));
        }
    }
}
