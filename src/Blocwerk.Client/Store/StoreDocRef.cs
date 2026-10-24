// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>One data document a cached wall row names: its sha and the feed cursor it was built at.</summary>
/// <param name="Sha">The document's content hash, its cache key.</param>
/// <param name="Cursor">The feed cursor read before its build, or null.</param>
public sealed record StoreDocRef(string Sha, string? Cursor);
