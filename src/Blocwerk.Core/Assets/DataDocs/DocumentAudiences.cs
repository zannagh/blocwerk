// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>What each <see cref="DocumentAudience"/> may see in a document, in one place.</summary>
public static class DocumentAudiences
{
    /// <summary>True only for the setters class: drafts stay out of every other class's documents.</summary>
    public static bool SeesDrafts(DocumentAudience audience) => audience == DocumentAudience.Setters;

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
