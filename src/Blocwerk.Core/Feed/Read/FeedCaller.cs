// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>What the request's principal is, as the web layer saw it (Core does not know the authentication schemes).</summary>
/// <param name="HasIdentity">True when the request carried an authenticated principal (a cookie or a token).</param>
/// <param name="IsApiKey">True when that principal is an API key.</param>
public sealed record FeedCaller(bool HasIdentity, bool IsApiKey);
