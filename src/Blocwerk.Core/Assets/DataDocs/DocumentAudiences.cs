// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>What each <see cref="DocumentAudience"/> may see in a document, in one place.</summary>
public static class DocumentAudiences
{
    /// <summary>
    /// True for the wall's members, setters included: the Server pages' rule (<c>WallService</c> loads drafts for every
    /// signed-in member, and only share-link and anonymous-kiosk reads leave them out, #180/#186), so offline shows what
    /// online shows. Share links and kiosk tablets, acting or not, never get a draft.
    /// </summary>
    public static bool SeesDrafts(DocumentAudience audience) => audience is DocumentAudience.Setters or DocumentAudience.Members;

    /// <summary>
    /// True for the same classes as <see cref="SeesDrafts"/>: the wall page lists archived boulders ("Archive") to every
    /// signed-in member, and its share-link branch leaves them out with the drafts.
    /// </summary>
    public static bool SeesArchived(DocumentAudience audience) => SeesDrafts(audience);

    /// <summary>
    /// True for the wall's members and setters only: the wall page's list rows carry the rating (and the detail page the
    /// ascent history) only for a member (<c>BoulderFeedbackService.GetBoulderListAsync</c>); its anonymous branch, which
    /// a share-link viewer takes signed in or not and a resting kiosk takes too, shows no rating and no sends. The kiosk
    /// class is every identity on the tablet, so it gets none even acting as a member, as with drafts.
    /// </summary>
    public static bool SeesStats(DocumentAudience audience) => audience is DocumentAudience.Setters or DocumentAudience.Members;

    /// <summary>
    /// True for the same classes as <see cref="SeesStats"/>: the reader's own ticks and favourites
    /// (<c>/api/me/walls/{w}/mine</c>), which the wall page shows only on its member branch. A share link and a kiosk
    /// tablet never get them, signed in or acting as a member.
    /// </summary>
    public static bool SeesMine(DocumentAudience audience) => audience is DocumentAudience.Setters or DocumentAudience.Members;

    /// <summary>
    /// True for the classes outside the wall (share link, kiosk): they are handed the wall's own feed mark
    /// (<see cref="FeedScopedMark"/>), never the global one, as the catch-up route hands them.
    /// </summary>
    public static bool IsOutside(DocumentAudience audience) => audience is DocumentAudience.Outsider or DocumentAudience.Kiosk;

    /// <summary>
    /// The feed's byline rule (<see cref="AuthorVisibility"/>) for the class, as the viewer the class stands for: members
    /// and setters see author ids; share links and the kiosk see one only for an author with an avatar; and the kiosk
    /// names consenting members only (<see cref="AuthorVisibility.MasksUnnamed"/>), as the resting tablet does. The class
    /// is every identity on the tablet, so a tablet acting as a member reads the resting bylines too, as with drafts.
    /// </summary>
    /// <remarks>The viewer carries no user id: the document is the class's, never one reader's.</remarks>
    public static AuthorVisibility Authors(DocumentAudience audience) => AuthorVisibility.For(audience switch
    {
        DocumentAudience.Setters or DocumentAudience.Members => new FeedViewer([FeedAudience.Wall, FeedAudience.Members], userId: null),
        DocumentAudience.Kiosk => FeedViewer.Kiosk(),
        _ => FeedViewer.Anonymous(),
    });

    /// <summary>The class of a granted feed viewer that is not a kiosk tablet.</summary>
    public static DocumentAudience Of(FeedViewer viewer) =>
        viewer.Audiences.Contains(FeedAudience.Setters) ? DocumentAudience.Setters
        : viewer.Audiences.Contains(FeedAudience.Members) ? DocumentAudience.Members
        : DocumentAudience.Outsider;
}
