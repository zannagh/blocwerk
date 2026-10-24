// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>One answer the sync loop got from the server, as client-sync.js reports it.</summary>
/// <param name="Status">The HTTP status; 0 when the request never got one (no network, a refused fetch).</param>
/// <param name="Reason">
/// On a 401: why no session is signed in (<c>X-Blocwerk-SignOut</c>, else the feed body's <c>reason</c>), or null.
/// </param>
/// <param name="RetryAfter">On a 429: the <c>Retry-After</c> seconds, or null.</param>
/// <param name="Body">The body of a 200, or null.</param>
public sealed record SyncResponse(int Status, string? Reason = null, double? RetryAfter = null, string? Body = null);
