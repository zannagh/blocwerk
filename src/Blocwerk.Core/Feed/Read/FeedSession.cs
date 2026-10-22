// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Feed;

/// <summary>Who is asking, as the request's scope knows it: the principal, the signed-in user, and the kiosk binding.</summary>
/// <param name="Caller">What the principal is.</param>
/// <param name="CurrentUser">Resolves the signed-in user.</param>
/// <param name="Kiosk">The session's kiosk state, or null outside an HTTP host.</param>
/// <param name="KioskKeys">Re-validates a kiosk tablet's key on every grant, so a revoked key stops reading at once.</param>
public sealed record FeedSession(
    FeedCaller Caller, ICurrentUserService CurrentUser, IKioskContext? Kiosk, IKioskKeyValidator? KioskKeys);
