// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>A refused <c>POST /api/device-link/token</c>, in the OAuth error shape (<c>{"error":"invalid_grant"}</c>).</summary>
public sealed record DeviceLinkTokenError(string Error);
