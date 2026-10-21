// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// The production mapper families, in one list so the app and the model-coverage guard see the same set. Each family
/// registers itself in its own file through one partial method: the wall-structure family (PR 1.2b) in
/// <c>FeedMappers.Structure.cs</c>, the social family (PR 1.2c) in <c>FeedMappers.Social.cs</c>.
/// </summary>
public static partial class FeedMappers
{
    /// <summary>Creates every production mapper.</summary>
    public static IReadOnlyList<IFeedMapper> Create()
    {
        var mappers = new List<IFeedMapper>();
        AddStructure(mappers);
        AddSocial(mappers);
        return mappers;
    }

    static partial void AddStructure(List<IFeedMapper> mappers);

    static partial void AddSocial(List<IFeedMapper> mappers);
}
