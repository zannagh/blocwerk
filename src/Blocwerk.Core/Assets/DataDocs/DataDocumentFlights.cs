// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// One in-flight build per <c>(wall, audience, kind)</c> (singleton): concurrent callers that find the same document
/// stale share the first caller's build instead of each building it, so a wall's worth of phones opening at once costs
/// one build per document. The entry is removed when its build completes, so the next stale read builds again.
/// </summary>
/// <remarks>
/// The shared build runs without any one caller's cancellation (a caller that gives up must not fail the others); each
/// caller stops waiting on its own token.
/// </remarks>
public sealed class DataDocumentFlights
{
    private readonly ConcurrentDictionary<(Guid WallId, DocumentAudience Audience, string Kind), Lazy<Task<StoredDataDocument?>>> flights = new();

    /// <summary>Gets the number of builds in flight.</summary>
    public int Count => flights.Count;

    /// <summary>Joins the build of the key in flight, or starts <paramref name="build"/> as it.</summary>
    public async Task<StoredDataDocument?> RunAsync(
        Guid wallId, DocumentAudience audience, string kind, Func<Task<StoredDataDocument?>> build, CancellationToken ct)
    {
        var key = (wallId, audience, kind);
        Lazy<Task<StoredDataDocument?>>? flight = null;
        flight = new Lazy<Task<StoredDataDocument?>>(
            () => RunAndLandAsync(key, flight!, build), LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = flights.GetOrAdd(key, flight);
        return await shared.Value.WaitAsync(ct);
    }

    // Removes exactly this flight, never a later one under the same key.
    private async Task<StoredDataDocument?> RunAndLandAsync(
        (Guid WallId, DocumentAudience Audience, string Kind) key,
        Lazy<Task<StoredDataDocument?>> flight,
        Func<Task<StoredDataDocument?>> build)
    {
        try
        {
            return await Task.Run(build);
        }
        finally
        {
            flights.TryRemove(KeyValuePair.Create(key, flight));
        }
    }
}
