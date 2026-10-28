// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// The data documents this instance has built, keyed by <c>(wall, audience, kind, sha)</c>, with the newest of each
/// <c>(wall, audience, kind)</c> as its current one (singleton, in memory, least recently used first out). Single
/// instance, like the rest of the app's caches.
/// </summary>
/// <remarks>
/// A document is found only under the audience it was built for: a sha another class was served (a setters document,
/// to somebody since demoted) is not there under the caller's class, so it is a miss, and a miss is the route's 404.
/// </remarks>
public sealed class DataDocumentStore
{
    /// <summary>How many documents are kept at most.</summary>
    public const int MaxEntries = 512;

    /// <summary>How many bytes (JSON plus its Brotli copy) are kept at most.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private readonly object gate = new();
    private readonly Dictionary<(Guid WallId, DocumentAudience Audience, string Kind, string Sha), LinkedListNode<StoredDataDocument>> entries = [];
    private readonly Dictionary<(Guid WallId, DocumentAudience Audience, string Kind), StoredDataDocument> current = [];
    private readonly LinkedList<StoredDataDocument> order = new();
    private long bytes;

    /// <summary>The newest document of the class and kind, if this instance has one.</summary>
    public StoredDataDocument? Current(Guid wallId, DocumentAudience audience, string kind)
    {
        lock (gate)
        {
            return current.GetValueOrDefault((wallId, audience, kind));
        }
    }

    /// <summary>The document named <paramref name="sha"/>, only if it was built for <paramref name="audience"/>.</summary>
    public StoredDataDocument? Find(Guid wallId, DocumentAudience audience, string kind, string sha)
    {
        lock (gate)
        {
            if (!entries.TryGetValue((wallId, audience, kind, sha), out var node))
            {
                return null;
            }

            order.Remove(node);
            order.AddFirst(node);
            return node.Value;
        }
    }

    /// <summary>
    /// Stores <paramref name="document"/> and makes it current unless a document built later is current already (two
    /// concurrent rebuilds: the later mark wins), or it was built in an epoch other than <paramref name="currentEpoch"/>
    /// (<see cref="StoredDataDocument.IsAtLeastAsNewAs"/>). A known sha keeps one entry, carrying the later mark.
    /// </summary>
    /// <returns>The class's current document after the publish; the published one when nothing is current.</returns>
    public StoredDataDocument Publish(StoredDataDocument document, Guid currentEpoch)
    {
        var key = (document.WallId, document.Audience, document.Kind, document.Sha);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var known))
            {
                order.Remove(known);
                order.AddFirst(known);
                if (document.IsAtLeastAsNewAs(known.Value, currentEpoch))
                {
                    bytes += document.Size - known.Value.Size;
                    known.Value = document;
                }
            }
            else
            {
                entries[key] = order.AddFirst(document);
                bytes += document.Size;
            }

            var slot = (document.WallId, document.Audience, document.Kind);
            var held = current.GetValueOrDefault(slot);
            if (document.BuildMark.Epoch == currentEpoch && (held is null || document.IsAtLeastAsNewAs(held, currentEpoch)))
            {
                current[slot] = entries[key].Value;
            }

            Trim();
            return current.TryGetValue(slot, out var now) ? now : document;
        }
    }

    private void Trim()
    {
        while (order.Count > 1 && (order.Count > MaxEntries || bytes > MaxBytes))
        {
            var last = order.Last!.Value;
            order.RemoveLast();
            entries.Remove((last.WallId, last.Audience, last.Kind, last.Sha));
            bytes -= last.Size;
            var slot = (last.WallId, last.Audience, last.Kind);
            if (current.TryGetValue(slot, out var held) && held.Sha == last.Sha)
            {
                current.Remove(slot);
            }
        }
    }
}
