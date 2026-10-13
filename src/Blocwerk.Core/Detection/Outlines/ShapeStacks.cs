// <copyright file="ShapeStacks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Which footprints of one panel are a hold and the hold it sits on: stacked as stored, or by the outlines the resolver
/// is about to fit (<see cref="HoldStacking"/>). Those pairs overlap on purpose, so the resolver leaves them out of
/// each other's way.
/// </summary>
internal static class ShapeStacks
{
    /// <summary>Per hold, the holds it sits on or carries; a hold in no stack is absent.</summary>
    /// <param name="holds">Every hold on the panel photo.</param>
    /// <returns>Hold id to the ids it is stacked with.</returns>
    public static Dictionary<Guid, HashSet<Guid>> Find(IReadOnlyList<HoldShapeInput> holds)
    {
        var areas = holds
            .Select(h => (h.Id, Area: new HoldArea(h.X, h.Y, h.Radius, h.Shape, null) { MayCarry = h.MayCarry }))
            .OrderBy(a => a.Area.MinX)
            .ToList();
        var found = new Dictionary<Guid, HashSet<Guid>>();
        var ids = holds.Select(h => h.Id).ToHashSet();
        foreach (var hold in holds.Where(h => h.StackedOn is { } lower && ids.Contains(lower)))
        {
            Note(found, hold.Id, hold.StackedOn!.Value);
            Note(found, hold.StackedOn.Value, hold.Id);
        }

        for (int i = 0; i < areas.Count; i++)
        {
            for (int j = i + 1; j < areas.Count && areas[j].Area.MinX <= areas[i].Area.MaxX; j++)
            {
                if (HoldStacking.SitsOn(areas[i].Area, areas[j].Area) || HoldStacking.SitsOn(areas[j].Area, areas[i].Area))
                {
                    Note(found, areas[i].Id, areas[j].Id);
                    Note(found, areas[j].Id, areas[i].Id);
                }
            }
        }

        return found;
    }

    private static void Note(Dictionary<Guid, HashSet<Guid>> found, Guid hold, Guid with)
    {
        if (!found.TryGetValue(hold, out var set))
        {
            found[hold] = set = [];
        }

        set.Add(with);
    }
}
