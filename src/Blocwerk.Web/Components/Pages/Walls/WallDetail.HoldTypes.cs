// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>A boulder's effective hold types on the list: the ones set on it, else the one its gripped holds show.</summary>
public partial class WallDetail
{
    private Dictionary<Guid, Hold> holdsById = [];
    private object? holdsByIdRef;

    private EffectiveHoldType? EffectiveHoldTypeOf(Boulder boulder)
    {
        if (!ReferenceEquals(holdsByIdRef, _holds))
        {
            holdsByIdRef = _holds;
            holdsById = _holds.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First());
        }

        var gripped = new List<HoldHandType?>();
        foreach (var bh in boulder.BoulderHolds)
        {
            if (holdsById.TryGetValue(bh.HoldId, out var hold) && BoulderHoldTypes.IsGripped(hold.Category, bh.Usage))
            {
                gripped.Add(hold.HandType);
            }
        }

        return BoulderHoldTypes.Effective(boulder.HoldTypes, gripped);
    }
}
