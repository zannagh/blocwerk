// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Data;

/// <summary>
/// Hashes a writer already knows, carried over with the bytes they belong to (a staged photo going live), so
/// <see cref="ContentHashStamp"/> takes them instead of hashing the bytes again. A carry holds only for the very array it
/// was made with: assign other bytes afterwards and the stamp hashes those. Cleared by every save.
/// </summary>
internal sealed class ContentHashCarries
{
    // A carry matches its bytes by array reference, so a photo array must never be mutated in place between the carry and
    // the save (the stale hash would be taken for the new bytes). Entities by reference: an entity type may override equality.
    private readonly Dictionary<object, Dictionary<string, (byte[] Bytes, byte[] Sha)>> carried = new(ReferenceEqualityComparer.Instance);

    public void Add(object entity, string hashProperty, byte[] bytes, byte[] sha)
    {
        if (!carried.TryGetValue(entity, out var columns))
        {
            columns = [];
            carried[entity] = columns;
        }

        columns[hashProperty] = (bytes, sha);
    }

    /// <summary>The carried hash of <paramref name="bytes"/>, when one was carried for exactly that array.</summary>
    public byte[]? Find(object entity, string hashProperty, byte[]? bytes) =>
        bytes is not null
        && carried.TryGetValue(entity, out var columns)
        && columns.TryGetValue(hashProperty, out var carry)
        && ReferenceEquals(carry.Bytes, bytes)
            ? carry.Sha
            : null;

    public void Clear() => carried.Clear();
}
