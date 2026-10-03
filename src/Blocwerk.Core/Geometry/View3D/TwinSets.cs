// <copyright file="TwinSets.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Union-find over hold-row indices that also tracks which panels each set already holds, so the
/// geometric fallback never folds two rows of one photo into one physical hold.
/// </summary>
internal sealed class TwinSets
{
    private readonly int[] parent;
    private readonly List<HashSet<Guid?>> panels;

    /// <summary>Initializes a new instance of the <see cref="TwinSets"/> class, one set per row.</summary>
    /// <param name="candidates">The rows.</param>
    public TwinSets(IReadOnlyList<HoldTwinCandidate> candidates)
    {
        parent = Enumerable.Range(0, candidates.Count).ToArray();
        panels = candidates.Select(c => new HashSet<Guid?> { c.Hold.WallPanelId }).ToList();
    }

    /// <summary>True when the sets of <paramref name="a"/> and <paramref name="b"/> share a panel.</summary>
    public bool SharePanel(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        return ra == rb || panels[ra].Overlaps(panels[rb]);
    }

    /// <summary>Joins the two sets; false when they already were one.</summary>
    public bool Union(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra == rb)
        {
            return false;
        }

        parent[rb] = ra;
        panels[ra].UnionWith(panels[rb]);
        return true;
    }

    /// <summary>The sets, each as its member indices, in first-member order.</summary>
    public IEnumerable<List<int>> Components() =>
        Enumerable.Range(0, parent.Length).GroupBy(Find).Select(g => g.ToList());

    private int Find(int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }
}
