// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Enums;

namespace Blocwerk.Client.ThreeD;

/// <summary>
/// A boulder's part in the 3D view, from the cached documents, as the Server's <c>Wall3DViewBuilder.RoleOf</c> gives it:
/// a start or top hold by its type, a foot-only one as a foothold, any other as a hand hold, and every other hold of
/// its foot colour (the "feet of one colour" rule) as a colour foothold.
/// </summary>
public static class Client3DBoulders
{
    /// <summary>The highlight of <paramref name="boulder"/>, or null for none.</summary>
    /// <param name="boulder">The boulder, or null (a wall's view).</param>
    /// <param name="holds">The wall's holds document, for the colour rule; null when not cached.</param>
    /// <returns>The highlight.</returns>
    public static Client3DBoulder? Of(BouldersDocumentBoulder? boulder, HoldsDocument? holds)
    {
        if (boulder is null)
        {
            return null;
        }

        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var hold in boulder.Holds)
        {
            roles.TryAdd(Id(hold.HoldId), RoleOf(hold));
        }

        var colorFoot = string.IsNullOrEmpty(boulder.FootColorOnly) || holds is null
            ? []
            : holds.Holds.Where(h => h.Color == boulder.FootColorOnly && !roles.ContainsKey(Id(h.Id))).Select(h => Id(h.Id)).ToList();
        return new Client3DBoulder(Id(boulder.Id), boulder.Name, roles, colorFoot);
    }

    private static string RoleOf(BouldersDocumentHold hold)
    {
        var type = Enum.TryParse<HoldType>(hold.Type, ignoreCase: true, out var t) ? t : HoldType.Normal;
        return type switch
        {
            HoldType.Start => "Start",
            HoldType.Top => "Top",
            _ => string.Equals(hold.Usage, nameof(HoldUsage.FootOnly), StringComparison.OrdinalIgnoreCase) ? "Foot" : "Hand",
        };
    }

    private static string Id(Guid id) => id.ToString();
}
