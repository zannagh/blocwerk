// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>Why the server closes a feed stream (<see cref="FeedStreamEvents"/>).</summary>
/// <param name="Reason">One of the <c>FeedStreamEvents.Bye*</c> reasons.</param>
public sealed record FeedStreamBye(string Reason);
