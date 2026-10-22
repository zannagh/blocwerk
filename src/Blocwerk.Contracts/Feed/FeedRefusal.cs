// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>The body of a refused feed request.</summary>
/// <param name="Error">One of <see cref="FeedRefusals"/>.</param>
/// <param name="Reason">
/// On a 401 only: why no session is signed in (<c>Blocwerk.Contracts.Assets.SignOutReasons</c>), so a <c>u-</c> device
/// knows whether to lock or wipe. Null (omitted) on every other refusal.
/// </param>
public sealed record FeedRefusal(string Error, string? Reason = null);
