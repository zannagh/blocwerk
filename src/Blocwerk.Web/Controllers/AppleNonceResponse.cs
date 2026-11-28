// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>Answer of <c>POST /api/device-link/apple/nonce</c>: the nonce whose SHA-256 goes into the Apple request.</summary>
public sealed record AppleNonceResponse(string Nonce);
