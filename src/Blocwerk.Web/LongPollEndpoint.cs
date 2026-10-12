// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web;

/// <summary>
/// Endpoint metadata for a route that holds its request open on purpose (a long poll). Such a request is slow by
/// design, so <see cref="SlowRequestLogging"/> leaves it out. Mark the route with <c>.WithMetadata(new LongPollEndpoint())</c>.
/// </summary>
public sealed class LongPollEndpoint
{
}
