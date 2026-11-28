// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Authentication.Apple;

/// <summary>
/// What Apple's form POST left for the rest of the round trip: its OAuth state, the raw nonce the identity token must
/// answer, and the name Apple sends on the first authorization only (null otherwise).
/// </summary>
public sealed record AppleCallbackTicket(string State, string RawNonce, string? Name, DateTimeOffset ExpiresAt);
