// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>One data document as <see cref="DataDocumentStore"/> keeps it.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="Audience">The class it was built for, and the only class it is served to.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Sha">The SHA-256 (lowercase hex) of <paramref name="Json"/>.</param>
/// <param name="Json">The document's bytes: what the sha names, and what is sent without Brotli.</param>
/// <param name="Brotli">The same bytes Brotli-compressed.</param>
/// <param name="BuildMark">
/// The global feed mark read <b>before</b> the build: every change at or below it is in the document, and a row of the
/// document's input kinds after it means the document may be stale (<see cref="DataDocumentFreshness"/>).
/// </param>
/// <param name="Cursor">
/// The cursor served with it: <paramref name="BuildMark"/> for the wall's members, the wall's own mark at or below it for
/// share and kiosk viewers (no row they may see lies between the two).
/// </param>
/// <param name="People">The users the document names by byline (<see cref="BuiltDataDocument.People"/>).</param>
/// <param name="Consented">Those of them it named by their kiosk consent (<see cref="BuiltDataDocument.Consented"/>).</param>
public sealed record StoredDataDocument(
    Guid WallId,
    DocumentAudience Audience,
    string Kind,
    string Sha,
    byte[] Json,
    byte[] Brotli,
    FeedCursor BuildMark,
    FeedCursor Cursor,
    IReadOnlyCollection<Guid> People,
    IReadOnlyCollection<Guid>? Consented = null)
{
    /// <summary>What the entry costs the store.</summary>
    public long Size => Json.LongLength + Brotli.LongLength;

    /// <summary>
    /// True when this may replace <paramref name="other"/> as current, given the feed's <paramref name="currentEpoch"/>:
    /// a document of another epoch never does (a build that started before a restore and finished after it), one of the
    /// current epoch always replaces one of another, and within the epoch the later build mark wins.
    /// </summary>
    public bool IsAtLeastAsNewAs(StoredDataDocument other, Guid currentEpoch)
    {
        if (BuildMark.Epoch != currentEpoch)
        {
            return false;
        }

        return other.BuildMark.Epoch != currentEpoch || BuildMark.Position >= other.BuildMark.Position;
    }
}
