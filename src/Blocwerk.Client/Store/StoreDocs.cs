// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>A wall's cached documents as client-store.js read them in one pass (its <c>docs()</c>).</summary>
/// <param name="Partition">The label they were read under; still the active one when the read ended.</param>
/// <param name="WallId">The wall.</param>
/// <param name="Docs">The documents by kind; a kind not cached is absent.</param>
public sealed record StoreDocs(string Partition, Guid WallId, IReadOnlyDictionary<string, StoreDocText> Docs);
