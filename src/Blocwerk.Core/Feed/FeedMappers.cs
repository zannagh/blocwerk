// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// The production mapper families, in one list so the app and the model-coverage guard see the same set. PR 1.2b adds the
/// wall-structure family, PR 1.2c the social one; until then nothing is mapped and the interceptor writes nothing.
/// </summary>
public static class FeedMappers
{
    /// <summary>Creates every production mapper.</summary>
    public static IReadOnlyList<IFeedMapper> Create() => [];
}
