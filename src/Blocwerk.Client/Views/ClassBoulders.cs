// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Documents;

namespace Blocwerk.Client.Views;

/// <summary>
/// The boulders a view shows of a cached wall. The documents are per class already, so an outsider's or a kiosk's
/// boulders document never carries a draft or an archived boulder; this drops them once more for those classes, in
/// depth, should one ever arrive.
/// </summary>
public static class ClassBoulders
{
    /// <summary>The boulders the wall's class may see.</summary>
    /// <param name="wall">The cached wall.</param>
    /// <returns>Its boulders, without drafts and archived ones unless the class is a member's.</returns>
    public static IReadOnlyList<BouldersDocumentBoulder> Of(CachedWall wall) => wall.Audience == StoreAudience.Member
        ? wall.VisibleBoulders
        : wall.VisibleBoulders.Where(b => b.Draft != true && !b.IsArchived).ToList();
}
