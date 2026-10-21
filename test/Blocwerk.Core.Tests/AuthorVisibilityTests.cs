// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="AuthorVisibility"/> exposes no more than the server-rendered pages: names to every viewer, author ids to
/// members, and to everyone else only for an author with an avatar (whose URL carries the id anyway).
/// </summary>
public sealed class AuthorVisibilityTests
{
    private static readonly FeedAuthor WithAvatar = new(Guid.NewGuid(), "Sam", HasAvatar: true, IsDeleted: false);
    private static readonly FeedAuthor WithoutAvatar = new(Guid.NewGuid(), "Kim", HasAvatar: false, IsDeleted: false);

    [Fact]
    public void AMember_SeesEveryAuthorsId()
    {
        var rule = AuthorVisibility.For(FeedViewer.Member(Guid.NewGuid()));

        Assert.Equal(WithoutAvatar.Id, rule.Describe(WithoutAvatar).Id);
        Assert.Equal(WithAvatar.Id, rule.Describe(WithAvatar).Id);
        Assert.Equal(GhostUser.Id, rule.Describe(null).Id);
    }

    [Fact]
    public void AViewerOutsideTheWall_SeesNames_ButIdsOnlyWhereTheAvatarShowsThem()
    {
        var anonymous = AuthorVisibility.For(FeedViewer.Anonymous());
        var signedInGuest = AuthorVisibility.For(new FeedViewer([Core.Enums.FeedAudience.Wall], Guid.NewGuid()));

        foreach (var rule in new[] { anonymous, signedInGuest })
        {
            Assert.Equal(("Kim", (Guid?)null), (rule.Describe(WithoutAvatar).Name, rule.Describe(WithoutAvatar).Id));
            Assert.Equal(("Sam", (Guid?)WithAvatar.Id), (rule.Describe(WithAvatar).Name, rule.Describe(WithAvatar).Id));
            Assert.Equal((PlaceholderIdentity.DisplayName, (Guid?)null), (rule.Describe(null).Name, rule.Describe(null).Id));
        }
    }
}
