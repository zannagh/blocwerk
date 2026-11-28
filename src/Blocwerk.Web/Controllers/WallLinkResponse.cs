// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>A link to hand out: an invite (<c>/join/{token}</c>) or a one-time visitor link (<c>/visit/{token}</c>).</summary>
/// <param name="Token">The token in the link.</param>
/// <param name="Url">The full link, as the web shows it.</param>
/// <param name="ExpiresAt">When a visitor link ends; null for an invite, which lasts until an admin makes a new one.</param>
public sealed record WallLinkResponse(string Token, string Url, DateTimeOffset? ExpiresAt);
