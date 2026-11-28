// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>What the request's principal is, as the web layer saw it (Core does not know the authentication schemes).</summary>
/// <param name="HasIdentity">True when the request carried an authenticated principal (a cookie or a token).</param>
/// <param name="IsApiKey">True when that principal is an API key.</param>
/// <param name="IsDeviceKey">True when that key is a linked device key (the companion app), which reads as its owner.</param>
/// <param name="ApiKeyId">The key's id, so a device key can be checked again while a stream is open.</param>
public sealed record FeedCaller(bool HasIdentity, bool IsApiKey, bool IsDeviceKey = false, Guid? ApiKeyId = null)
{
    /// <summary>True for an API key the feed and the documents refuse: any key but a linked device key.</summary>
    public bool IsRefusedKey => IsApiKey && !IsDeviceKey;
}
