// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>A boulder's visibility state as it is when a row about it is delivered.</summary>
/// <param name="IsDraft">Whether it is a draft now.</param>
/// <param name="IsArchived">Whether it is archived now.</param>
public sealed record FeedBoulderState(bool IsDraft, bool IsArchived);
