// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Serialization;

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>POST /api/v1/me/apple</c>.</summary>
public sealed record AppleLinkRequest(
    [property: JsonPropertyName("identityToken")] string? IdentityToken,
    [property: JsonPropertyName("nonce")] string? Nonce);
