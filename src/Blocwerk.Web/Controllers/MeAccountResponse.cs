// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>Answer of <c>GET /api/v1/me/account</c>.</summary>
/// <param name="Id">The owner's user id.</param>
/// <param name="DisplayName">The name the app shows.</param>
/// <param name="Email">The owner's e-mail, or null.</param>
/// <param name="AvatarUrl">The path of <c>GET /api/v1/me/avatar</c>, or null without an avatar.</param>
/// <param name="KeyName">The name of the key the request came with (for a linked device, "iPhone: …").</param>
public sealed record MeAccountResponse(Guid Id, string DisplayName, string? Email, string? AvatarUrl, string? KeyName);
