// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// Which kiosk facts (<see cref="FeedKioskFacts"/>) a read has to load for the viewers it is delivered to: the activity
/// facts for any kiosk tablet, the kiosk consent of the authors only for a resting one. A read no kiosk is listening to
/// loads neither, so a wall without a tablet pays nothing for them.
/// </summary>
/// <param name="Rows">Whether a kiosk viewer may read activity rows (<see cref="FeedViewer.MemberKinds"/>).</param>
/// <param name="Bylines">Whether a resting kiosk viewer needs the authors' consent (<see cref="FeedViewer.IsRestingKiosk"/>).</param>
public readonly record struct FeedKioskNeeds(bool Rows, bool Bylines)
{
    /// <summary>Gets the needs of no kiosk at all.</summary>
    public static FeedKioskNeeds None => default;

    /// <summary>The needs of <paramref name="viewers"/>.</summary>
    public static FeedKioskNeeds Of(IEnumerable<FeedViewer> viewers)
    {
        var needs = None;
        foreach (var viewer in viewers)
        {
            needs = new FeedKioskNeeds(needs.Rows || viewer.MemberKinds.Count > 0, needs.Bylines || viewer.IsRestingKiosk);
        }

        return needs;
    }
}
