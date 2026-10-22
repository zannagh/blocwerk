// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Feed;

/// <summary>
/// What a viewer sees of the person behind a change (design §5.4, N4), matching the server-rendered pages. Every viewer who
/// may see an item sees the byline (<c>User.Name</c>, and the avatar), share and anonymous kiosk viewers too, and an
/// unknown, erased or system author renders as <see cref="PlaceholderIdentity.DisplayName"/>, credited to the Ghost row.
/// The author's user id goes to members of the wall; a viewer outside it gets it only for an author with an avatar,
/// because the pages expose exactly that id in the avatar URL (<c>UserAvatar</c> in <c>BoulderComments</c> and
/// <c>BoulderActivityFeed</c> on the share page, the kiosk user picker) and nothing else. The UI moves onto this function
/// with the live leaf lists (PR 1.5), so feed and page can never disagree.
/// </summary>
public sealed class AuthorVisibility
{
    private static readonly AuthorVisibility Members = new(showsIds: true, masksUnnamed: false);
    private static readonly AuthorVisibility Outsiders = new(showsIds: false, masksUnnamed: false);
    private static readonly AuthorVisibility RestingKiosk = new(showsIds: false, masksUnnamed: true);
    private readonly bool showsIds;

    private AuthorVisibility(bool showsIds, bool masksUnnamed)
    {
        this.showsIds = showsIds;
        MasksUnnamed = masksUnnamed;
    }

    /// <summary>
    /// Gets a value indicating whether an author the viewer may not name (<see cref="Describe(FeedAuthor?, bool)"/>) reads
    /// as <see cref="KioskBylines.PlaceholderName"/>: the resting kiosk's rule.
    /// </summary>
    public bool MasksUnnamed { get; }

    /// <summary>
    /// The rule for <paramref name="viewer"/>: members (and setters) see author ids, everyone else only where the avatar
    /// shows it, and the resting kiosk names consenting members only (<see cref="KioskBylines"/>).
    /// </summary>
    public static AuthorVisibility For(FeedViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return viewer.Audiences.Contains(FeedAudience.Members) ? Members
            : viewer.IsRestingKiosk ? RestingKiosk
            : Outsiders;
    }

    /// <summary>The actor as this viewer sees them; <paramref name="author"/> is null when the user row is gone.</summary>
    public FeedActor Describe(FeedAuthor? author) => Describe(author, named: true);

    /// <summary>
    /// The actor as this viewer sees them, where <paramref name="named"/> says whether a <see cref="MasksUnnamed"/> viewer
    /// may name this author here (they consented to the kiosk, or the entry is a setter's). Ghost stays Ghost.
    /// </summary>
    public FeedActor Describe(FeedAuthor? author, bool named)
    {
        if (author is null || author.IsDeleted || GhostUser.Is(author.Id) || string.IsNullOrWhiteSpace(author.Name))
        {
            return new FeedActor(showsIds ? GhostUser.Id : null, PlaceholderIdentity.DisplayName, HasAvatar: false, AvatarSha: null);
        }

        if (MasksUnnamed && !named)
        {
            return new FeedActor(null, KioskBylines.PlaceholderName, HasAvatar: false, AvatarSha: null);
        }

        var id = showsIds || author.HasAvatar ? author.Id : (Guid?)null;
        return new FeedActor(id, author.Name, author.HasAvatar, AvatarSha: null);
    }
}
