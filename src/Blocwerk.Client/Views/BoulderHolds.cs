// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds;
using Blocwerk.Shared.Ui.Overlay;

namespace Blocwerk.Client.Views;

/// <summary>
/// A boulder's holds as the boulder detail draws and counts them (the Server's BoulderDetail and BoulderRulesSummary):
/// each hold's type and usage from the boulders document, the boulder colours, the foot-only set, the panels that carry
/// them, and how many of them the live holds document no longer has (a historic boulder's).
/// </summary>
public sealed class BoulderHolds
{
    private readonly Dictionary<Guid, (HoldType Type, HoldUsage Usage)> roles;

    /// <summary>Initializes a new instance of the <see cref="BoulderHolds"/> class.</summary>
    /// <param name="boulder">The boulder.</param>
    /// <param name="holds">The wall's live holds, or null when not cached.</param>
    /// <param name="grid">The wall's photos, which place its holds.</param>
    public BoulderHolds(BouldersDocumentBoulder boulder, HoldsDocument? holds, WallPanelGrid grid)
    {
        roles = boulder.Holds
            .GroupBy(h => h.HoldId)
            .ToDictionary(g => g.Key, g => (Parse(g.First().Type, HoldType.Normal), Parse(g.First().Usage, HoldUsage.HandAndFoot)));
        Ids = roles.Keys.ToHashSet();
        FootOnly = roles.Where(r => r.Value.Usage == HoldUsage.FootOnly).Select(r => r.Key).ToHashSet();
        Saved = holds is not null;
        Panels = grid.PanelsOf(Ids);
        Missing = holds is null ? 0 : Ids.Count - holds.Holds.Count(h => Ids.Contains(h.Id));
        Starts = roles.Values.Count(r => r.Type == HoldType.Start);
        Tops = roles.Values.Count(r => r.Type == HoldType.Top);
        HandOnly = roles.Values.Count(r => r.Usage == HoldUsage.HandOnly);
        FootOnlyCount = FootOnly.Count;
        ColorRuleMatches = boulder.FootColorOnly is { Length: > 0 } color && holds is not null && holds.Holds.Any(h => h.Color == color);
    }

    /// <summary>Gets the boulder's holds.</summary>
    public IReadOnlySet<Guid> Ids { get; }

    /// <summary>Gets its foot-only holds.</summary>
    public IReadOnlySet<Guid> FootOnly { get; }

    /// <summary>Gets a value indicating whether the wall's holds are on the device (else nothing is known of them).</summary>
    public bool Saved { get; }

    /// <summary>Gets the panels its live holds are drawn on.</summary>
    public IReadOnlySet<Guid> Panels { get; }

    /// <summary>Gets how many of its holds the cached live holds document does not have (0 when none is cached).</summary>
    public int Missing { get; }

    /// <summary>Gets the start holds.</summary>
    public int Starts { get; }

    /// <summary>Gets the top holds.</summary>
    public int Tops { get; }

    /// <summary>Gets the hand-only holds.</summary>
    public int HandOnly { get; }

    /// <summary>Gets the foot-only holds.</summary>
    public int FootOnlyCount { get; }

    /// <summary>Gets a value indicating whether a wall hold has the colour of the boulder's "feet of one colour" rule.</summary>
    public bool ColorRuleMatches { get; }

    /// <summary>The boulder colours of one of its holds (the Server's BoulderHoldColorOf).</summary>
    /// <param name="holdId">The hold.</param>
    /// <returns>Fill and stroke.</returns>
    public (string Fill, string Stroke) ColorOf(Guid holdId) => roles.TryGetValue(holdId, out var r)
        ? (BoulderHoldColors.FillFor(r.Type, r.Usage), BoulderHoldColors.Outline(r.Type, r.Usage))
        : (BoulderHoldColors.Fill(HoldType.Normal), BoulderHoldColors.Normal);

    private static T Parse<T>(string? value, T fallback)
        where T : struct, Enum => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
