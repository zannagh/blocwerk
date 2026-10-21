// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Feed;

/// <summary>
/// What a viewer sees of the person behind a change (design §5.4, N4). Today every viewer who may see an item sees the same
/// byline the server-rendered UI shows (<c>User.Name</c> and the avatar, for share and anonymous kiosk viewers too), and an
/// unknown, erased or system author renders as <see cref="PlaceholderIdentity.DisplayName"/>, credited to the Ghost row.
/// The viewer is the seam for a later rule that hides names from some viewers; the UI moves onto this function with the
/// live leaf lists (PR 1.5), so feed and page can never disagree.
/// </summary>
public sealed class AuthorVisibility
{
    private static readonly AuthorVisibility Everyone = new();

    private AuthorVisibility()
    {
    }

    /// <summary>The rule for <paramref name="viewer"/>.</summary>
    public static AuthorVisibility For(FeedViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return Everyone;
    }

    /// <summary>The actor as this viewer sees them; <paramref name="author"/> is null when the user row is gone.</summary>
    public FeedActor Describe(FeedAuthor? author)
    {
        if (author is null || author.IsDeleted || GhostUser.Is(author.Id) || string.IsNullOrWhiteSpace(author.Name))
        {
            return new FeedActor(GhostUser.Id, PlaceholderIdentity.DisplayName, HasAvatar: false, AvatarSha: null);
        }

        return new FeedActor(author.Id, author.Name, author.HasAvatar, AvatarSha: null);
    }
}
