// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>One stream's caller, as granted at connect.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="Viewer">The viewer the gate granted; the hub subscription narrows to it.</param>
/// <param name="After">The client's cursor (<c>Last-Event-ID</c>, else <c>?after=</c>), or null.</param>
/// <param name="Recheck">Decides the grant again; null when it is gone.</param>
internal sealed record FeedStreamRequest(
    Guid WallId, FeedViewer Viewer, string? After, Func<CancellationToken, Task<FeedViewer?>> Recheck);
