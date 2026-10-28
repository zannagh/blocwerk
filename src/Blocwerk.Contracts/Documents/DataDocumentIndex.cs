// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>
/// <c>GET /api/walls/{w}/docs</c>: the caller's audience documents of the wall. A client fetches the docs whose sha
/// changed and replays the feed from <see cref="Cursor"/>, the oldest of the docs' cursors, so a write that one document
/// already reflects and another does not is replayed (replay is idempotent by id).
/// </summary>
/// <param name="Cursor">The minimum of the docs' cursors.</param>
/// <param name="Docs">One entry per kind, in <see cref="DataDocumentKinds.All"/> order.</param>
public sealed record DataDocumentIndex(string Cursor, IReadOnlyList<DataDocumentEntry> Docs);
