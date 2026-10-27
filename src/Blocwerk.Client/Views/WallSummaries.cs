// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Documents;

namespace Blocwerk.Client.Views;

/// <summary>
/// The wall list's cards (PR 3.11 review): each card parses only its wall's <c>wall</c> and <c>boulders</c> documents,
/// never its holds, gallery or comments, and keeps only the summary, by the partition and ledger row it was read from (a new row key
/// reads it again). Only the walls of the viewer's own list are listed: a member's rows, and a kiosk's bound wall. A
/// wall a signed-in device cached through a share link is no wall of theirs, and without its token (the device keeps
/// only a hash) it could not be opened.
/// </summary>
public sealed class WallSummaries
{
    private readonly ClientStore store;
    private readonly IClientStoreSource source;
    private readonly Dictionary<(string Partition, Guid WallId, string? Key), WallSummary> read = [];

    /// <summary>Initializes a new instance of the <see cref="WallSummaries"/> class.</summary>
    /// <param name="store">The store (its partition and rows).</param>
    /// <param name="source">The device cache.</param>
    public WallSummaries(ClientStore store, IClientStoreSource source)
    {
        this.store = store;
        this.source = source;
    }

    /// <summary>The cards of the viewer's walls, by name.</summary>
    /// <returns>The summaries; none unless the store is ready.</returns>
    public async Task<IReadOnlyList<WallSummary>> ListAsync()
    {
        var partition = store.Partition;
        var cards = new List<WallSummary>();
        foreach (var row in store.Walls.Where(r => store.AudienceOf(r.WallId) != StoreAudience.Outsider))
        {
            if (partition is not null && read.TryGetValue((partition, row.WallId, row.Key), out var known))
            {
                cards.Add(known);
                continue;
            }

            if (partition is not null && await source.DocsAsync(partition, row.WallId) is { } docs && docs.Partition == partition
                && Summarise(docs, store.AudienceOf(row.WallId)) is { } card)
            {
                read[(partition, row.WallId, row.Key)] = card;
                cards.Add(card);
            }
        }

        return cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A card from a wall's documents, parsing only the wall and boulders ones.</summary>
    /// <param name="docs">The wall's cached documents.</param>
    /// <param name="audience">The class they were built for.</param>
    /// <returns>The card, or null without a usable wall document.</returns>
    public static WallSummary? Summarise(StoreDocs docs, StoreAudience audience)
    {
        if (!docs.Docs.TryGetValue(DataDocumentKinds.Wall, out var wallText)
            || DataDocumentReader.Read(DataDocumentKinds.Wall, wallText.Text, docs.WallId) is not WallDocument wall)
        {
            return null;
        }

        int? count = null;
        if (docs.Docs.TryGetValue(DataDocumentKinds.Boulders, out var bouldersText)
            && DataDocumentReader.Read(DataDocumentKinds.Boulders, bouldersText.Text, docs.WallId) is BouldersDocument boulders)
        {
            count = boulders.Boulders.Count(b => !b.IsArchived && (audience == StoreAudience.Member || b.Draft != true));
        }

        var photo = wall.PhotoSha ?? wall.Panels.OrderBy(p => p.Row).ThenBy(p => p.Col).FirstOrDefault(p => p.Sha is not null)?.Sha;
        return new WallSummary(docs.WallId, wall.Name ?? string.Empty, photo, count);
    }
}
