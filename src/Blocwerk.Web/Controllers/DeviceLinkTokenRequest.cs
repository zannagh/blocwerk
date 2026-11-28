// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Serialization;

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>POST /api/device-link/token</c>.</summary>
public sealed record DeviceLinkTokenRequest(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("code_verifier")] string? CodeVerifier,
    [property: JsonPropertyName("device")] string? Device);
