// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Assets.Documents;

/// <summary>
/// The view3d documents this instance has built, by wall and sha (singleton, in memory, least recently used first out).
/// A page publishes the document of the view it is about to mount, so the browser's fetch of <c>/d/view3d/{sha}</c> that
/// follows is answered from here; a miss (a restart, an eviction) is rebuilt by <see cref="View3DDocumentService"/>.
/// </summary>
/// <remarks>
/// Every entry's sha is the SHA-256 of its own bytes, computed here from those bytes: the store can hold an old document
/// of a wall, never a document under a sha that names other bytes. Single instance, like the rest of the app's caches.
/// </remarks>
public sealed class View3DDocumentStore
{
    /// <summary>How many documents are kept at most.</summary>
    public const int MaxEntries = 128;

    /// <summary>How many bytes (JSON plus its Brotli copy) are kept at most.</summary>
    public const long MaxBytes = 96L * 1024 * 1024;

    /// <summary>How long a rebuild of one wall's document blocks the next (a miss for an unknown sha costs no second build).</summary>
    public static readonly TimeSpan RebuildWindow = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private readonly Dictionary<(Guid WallId, string Sha), LinkedListNode<StoredView3DDocument>> entries = [];
    private readonly LinkedList<StoredView3DDocument> order = new();
    private readonly Dictionary<(Guid WallId, Guid? BoulderId), DateTimeOffset> rebuilds = [];
    private long bytes;

    /// <summary>Stores the document of <paramref name="view"/> (once per sha) and returns its sha and the boulder overlay.</summary>
    /// <param name="view">A built, access-checked view.</param>
    /// <returns>The published document.</returns>
    public View3DPublished Publish(Wall3DView view)
    {
        var (document, overlay) = View3DDocument.Split(view);
        var json = View3DDocument.Serialize(document);
        var sha = ContentHash.ToHex(ContentHash.Of(json))!;
        lock (gate)
        {
            if (entries.TryGetValue((view.WallId, sha), out var known))
            {
                order.Remove(known);
                order.AddFirst(known);
                return new View3DPublished(sha, json.LongLength, overlay, View3DDocument.TokenOf(view));
            }
        }

        var stored = new StoredView3DDocument(view.WallId, sha, json, Compress(json));
        lock (gate)
        {
            if (!entries.ContainsKey((view.WallId, sha)))
            {
                entries[(view.WallId, sha)] = order.AddFirst(stored);
                bytes += stored.Size;
                Trim();
            }
        }

        return new View3DPublished(sha, json.LongLength, overlay, View3DDocument.TokenOf(view));
    }

    /// <summary>The stored document of <paramref name="wallId"/> named <paramref name="sha"/>, if this instance has it.</summary>
    public StoredView3DDocument? Find(Guid wallId, string sha)
    {
        lock (gate)
        {
            if (!entries.TryGetValue((wallId, sha), out var node))
            {
                return null;
            }

            order.Remove(node);
            order.AddFirst(node);
            return node.Value;
        }
    }

    /// <summary>
    /// True when a rebuild of this wall's (and boulder's) document may start now; it then blocks the next one for
    /// <see cref="RebuildWindow"/>, so random shas under a share link cannot each cost a build.
    /// </summary>
    public bool TryBeginRebuild(Guid wallId, Guid? boulderId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (rebuilds.TryGetValue((wallId, boulderId), out var at) && now - at < RebuildWindow)
            {
                return false;
            }

            if (rebuilds.Count >= MaxEntries * 4)
            {
                rebuilds.Clear();
            }

            rebuilds[(wallId, boulderId)] = now;
            return true;
        }
    }

    private static byte[] Compress(byte[] json)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(json);
        }

        return output.ToArray();
    }

    private void Trim()
    {
        while (order.Count > 1 && (order.Count > MaxEntries || bytes > MaxBytes))
        {
            var last = order.Last!.Value;
            order.RemoveLast();
            entries.Remove((last.WallId, last.Sha));
            bytes -= last.Size;
        }
    }
}
