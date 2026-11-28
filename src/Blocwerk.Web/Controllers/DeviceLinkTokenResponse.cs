// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>Answer of <c>POST /api/device-link/token</c>: the full key, shown this once, its id and its name.</summary>
public sealed record DeviceLinkTokenResponse(string Token, Guid KeyId, string Name);
