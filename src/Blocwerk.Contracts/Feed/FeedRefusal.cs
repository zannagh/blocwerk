// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>The body of a refused feed request.</summary>
/// <param name="Error">One of <see cref="FeedRefusals"/>.</param>
public sealed record FeedRefusal(string Error);
