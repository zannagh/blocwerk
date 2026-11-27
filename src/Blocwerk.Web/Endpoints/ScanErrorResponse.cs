// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints;

/// <summary>A refused scan request: one sentence, and for a bad manifest or an incomplete commit the items it is about.</summary>
/// <param name="Message">What went wrong.</param>
/// <param name="Problems">The manifest's problems, or the files still missing at commit; empty otherwise.</param>
public sealed record ScanErrorResponse(string Message, IReadOnlyList<string> Problems);
