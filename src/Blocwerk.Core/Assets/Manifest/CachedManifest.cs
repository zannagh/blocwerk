// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>One built manifest in <see cref="WallManifestCache"/>.</summary>
/// <param name="Manifest">The manifest, without a cursor.</param>
/// <param name="Cursor">
/// The feed cursor taken before the build: every change at or below it is in the manifest. The entry is checked against
/// the wall's rows after it; null means the cursor could not be read, so the entry is never reused.
/// </param>
/// <param name="BuiltAt">When it was built, for the age limit.</param>
public sealed record CachedManifest(WallManifest Manifest, FeedCursor? Cursor, DateTimeOffset BuiltAt)
{
    /// <summary>The manifest as served: with the cursor it reflects.</summary>
    public WallManifest Served => Manifest with { Cursor = Cursor?.ToString() };
}
