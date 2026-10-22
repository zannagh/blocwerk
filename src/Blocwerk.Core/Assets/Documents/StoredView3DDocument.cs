// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Documents;

/// <summary>One view3d document as <see cref="View3DDocumentStore"/> keeps it.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="Sha">The SHA-256 (lowercase hex) of <paramref name="Json"/>.</param>
/// <param name="Json">The document's bytes: what the sha names, and what is sent without Brotli.</param>
/// <param name="Brotli">The same bytes Brotli-compressed, sent to a browser that accepts <c>br</c>.</param>
public sealed record StoredView3DDocument(Guid WallId, string Sha, byte[] Json, byte[] Brotli)
{
    /// <summary>What the entry costs the store.</summary>
    public long Size => Json.LongLength + Brotli.LongLength;
}
