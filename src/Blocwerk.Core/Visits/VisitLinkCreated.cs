// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Visits;

/// <summary>A freshly made visitor link: the token exists in full only here.</summary>
/// <param name="Token">The token for the <c>/visit/{token}</c> address.</param>
/// <param name="ExpiresAt">When the link, and any visit made with it, ends.</param>
public sealed record VisitLinkCreated(string Token, DateTimeOffset ExpiresAt);
