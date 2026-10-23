// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Serves the data documents (Phase 3 plan §1): the <c>/api/walls/{w}/docs</c> index and <c>/d/{kind}/{sha}</c>. The
/// caller's class comes from <see cref="DocumentAudienceGate"/>; the documents come from <see cref="DataDocumentStore"/>,
/// rebuilt when stale (<see cref="DataDocumentFreshness"/>) or missing.
/// </summary>
/// <remarks>
/// <para>
/// Each build reads the feed mark <b>before</b> it reads the wall, so a write that commits during the build is either in
/// the document or after its mark: never lost, at worst replayed. The index answers the minimum of its documents'
/// cursors, so a client that keeps an older document replays from that one's cursor.
/// </para>
/// <para>
/// A sha is served only when the store holds it under the caller's own class; a miss rebuilds the caller's current
/// document of the kind (no more than a stale index would) and serves it only if it is the sha asked for. Concurrent
/// callers that find a document stale share one build (<see cref="DataDocumentFlights"/>).
/// </para>
/// </remarks>
public sealed class DataDocumentService(
    RootDbContextFactory dbContextFactory,
    DataDocumentBuilder builder,
    DataDocumentStore store,
    DataDocumentFlights flights,
    IWallFeed feed)
{
    /// <summary>The caller's class on <paramref name="wallId"/>, or why it has none.</summary>
    public async Task<DocumentAccess> AccessAsync(Guid wallId, string? shareToken, FeedSession session, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await DocumentAudienceGate.DecideAsync(db, wallId, shareToken, session, ct);
    }

    /// <summary>The class's index of the wall, or null when the wall is gone.</summary>
    public async Task<DataDocumentIndex?> IndexAsync(Guid wallId, DocumentAudience audience, CancellationToken ct)
    {
        var documents = new List<StoredDataDocument>();
        foreach (var kind in DataDocumentKinds.All)
        {
            if (await EnsureAsync(wallId, audience, kind, ct) is not { } document)
            {
                return null;
            }

            documents.Add(document);
        }

        var oldest = documents.MinBy(d => d.Cursor.Position)!.Cursor;
        return new DataDocumentIndex(
            oldest.ToString(),
            documents.Select(d => new DataDocumentEntry(d.Kind, d.Sha, d.Brotli.LongLength, d.Cursor.ToString())).ToList());
    }

    /// <summary>The class's document named <paramref name="sha"/>, or null for every refusal alike (the route's 404).</summary>
    public async Task<StoredDataDocument?> GetAsync(Guid wallId, DocumentAudience audience, string kind, string sha, CancellationToken ct)
    {
        if (!DataDocumentKinds.IsKnown(kind) || !AssetUrls.IsSha(sha))
        {
            return null;
        }

        if (store.Find(wallId, audience, kind, sha) is { } found)
        {
            return found;
        }

        return await EnsureAsync(wallId, audience, kind, ct) is { } current && current.Sha == sha ? current : null;
    }

    private async Task<StoredDataDocument?> EnsureAsync(Guid wallId, DocumentAudience audience, string kind, CancellationToken ct)
    {
        await using (var db = dbContextFactory.CreateDbContext())
        {
            if (store.Current(wallId, audience, kind) is { } held && await DataDocumentFreshness.IsFreshAsync(db, held, ct))
            {
                return held;
            }
        }

        return await flights.RunAsync(wallId, audience, kind, () => BuildAsync(wallId, audience, kind), ct);
    }

    // Shared by every caller in flight, so it runs on no caller's token.
    private async Task<StoredDataDocument?> BuildAsync(Guid wallId, DocumentAudience audience, string kind)
    {
        var mark = await feed.CurrentAsync();
        if (await builder.BuildAsync(wallId, audience, kind, CancellationToken.None) is not { } built)
        {
            return null;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var cursor = DocumentAudiences.IsOutside(audience)
            ? await FeedScopedMark.OfAsync(db, FeedFilter.ForWall(wallId, [FeedAudience.Wall], null), mark, CancellationToken.None)
            : mark;
        var epoch = (await FeedEpochStore.ReadAsync(db, CancellationToken.None))?.Id ?? mark.Epoch;
        var json = built.Json;
        var sha = ContentHash.ToHex(ContentHash.Of(json))!;
        var document = new StoredDataDocument(wallId, audience, kind, sha, json, DocumentBrotli.Compress(json), mark, cursor, built.People, built.Consented);
        return store.Publish(document, epoch);
    }
}
