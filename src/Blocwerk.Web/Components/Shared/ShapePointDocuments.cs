// <copyright file="ShapePointDocuments.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Runtime.CompilerServices;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// A stored outline (<see cref="ShapePoint"/>s) as the shared hold renderer takes it (<see cref="DocumentPoint"/>s).
/// Converted once per list and kept with it, so an editor rendering hundreds of holds allocates nothing per render;
/// the editors move points in place, so a kept copy is checked against the list's current values and rebuilt when one
/// moved. A kept copy lives exactly as long as its list: the weak table holds the list weakly, so the copy goes with
/// the list (a hold reloaded or discarded) and never keeps a list alive.
/// </summary>
public static class ShapePointDocuments
{
    private static readonly ConditionalWeakTable<object, IReadOnlyList<DocumentPoint>> Rings = new();
    private static readonly ConditionalWeakTable<object, IReadOnlyList<IReadOnlyList<DocumentPoint>>> Holes = new();

    /// <summary>The outline as document points, or null.</summary>
    /// <param name="ring">The stored outline, or null.</param>
    /// <returns>The same points, as document points.</returns>
    public static IReadOnlyList<DocumentPoint>? AsDocumentRing(this IReadOnlyList<ShapePoint>? ring)
    {
        if (ring is null)
        {
            return null;
        }

        if (Rings.TryGetValue(ring, out var kept) && Same(kept, ring))
        {
            return kept;
        }

        var converted = Convert(ring);
        Rings.AddOrUpdate(ring, converted);
        return converted;
    }

    /// <summary>The hole rings as document points, or null.</summary>
    /// <param name="rings">The stored hole rings, or null.</param>
    /// <returns>The same rings, as document points.</returns>
    public static IReadOnlyList<IReadOnlyList<DocumentPoint>>? AsDocumentRings(this IReadOnlyList<IReadOnlyList<ShapePoint>>? rings)
    {
        if (rings is null)
        {
            return null;
        }

        if (Holes.TryGetValue(rings, out var kept) && SameRings(kept, rings))
        {
            return kept;
        }

        IReadOnlyList<IReadOnlyList<DocumentPoint>> converted = [.. rings.Select(Convert)];
        Holes.AddOrUpdate(rings, converted);
        return converted;
    }

    private static List<DocumentPoint> Convert(IReadOnlyList<ShapePoint> ring) => [.. ring.Select(p => new DocumentPoint(p.Dx, p.Dy))];

    private static bool SameRings(IReadOnlyList<IReadOnlyList<DocumentPoint>> kept, IReadOnlyList<IReadOnlyList<ShapePoint>> rings)
    {
        if (kept.Count != rings.Count)
        {
            return false;
        }

        for (var i = 0; i < rings.Count; i++)
        {
            if (!Same(kept[i], rings[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Same(IReadOnlyList<DocumentPoint> kept, IReadOnlyList<ShapePoint> ring)
    {
        if (kept.Count != ring.Count)
        {
            return false;
        }

        for (var i = 0; i < ring.Count; i++)
        {
            if (kept[i].Dx != ring[i].Dx || kept[i].Dy != ring[i].Dy)
            {
                return false;
            }
        }

        return true;
    }
}
