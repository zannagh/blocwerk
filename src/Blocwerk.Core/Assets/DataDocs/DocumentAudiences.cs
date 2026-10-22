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
    /// True for the classes outside the wall (share link, kiosk): they are handed the wall's own feed mark
    /// (<see cref="FeedScopedMark"/>), never the global one, as the catch-up route hands them.
    /// </summary>
    public static bool IsOutside(DocumentAudience audience) => audience is DocumentAudience.Outsider or DocumentAudience.Kiosk;

    /// <summary>The class of a granted feed viewer that is not a kiosk tablet.</summary>
    public static DocumentAudience Of(FeedViewer viewer) =>
        viewer.Audiences.Contains(FeedAudience.Setters) ? DocumentAudience.Setters
        : viewer.Audiences.Contains(FeedAudience.Members) ? DocumentAudience.Members
        : DocumentAudience.Outsider;
}
