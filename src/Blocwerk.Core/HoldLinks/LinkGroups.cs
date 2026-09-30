// <copyright file="LinkGroups.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.HoldLinks;

/// <summary>
/// The holds already tied together by stored links (transitively), with the panels each group covers. A pair may be
/// suggested (or linked) only when it is not one group yet and linking it would not give a group two holds on one
/// panel. The panels come from EVERY live panel hold, placed on the 3D model or not.
/// </summary>
internal sealed class LinkGroups
{
    private readonly Dictionary<Guid, Guid> parent = [];
    private readonly Dictionary<Guid, HashSet<Guid>> panels = [];

    /// <param name="panelOf">Every live hold's panel photo (hold id → panel id).</param>
    /// <param name="links">The wall's stored links.</param>
    public LinkGroups(IReadOnlyDictionary<Guid, Guid> panelOf, IEnumerable<(Guid A, Guid B)> links)
    {
        foreach (var (a, b) in links)
        {
            parent[Find(a)] = Find(b);
        }

        foreach (var (hold, panel) in panelOf)
        {
            var root = Find(hold);
            if (!panels.TryGetValue(root, out var set))
            {
                panels[root] = set = [];
            }

            set.Add(panel);
        }
    }

    public bool MayLink(Guid a, Guid panelA, Guid b, Guid panelB)
    {
        var ra = Find(a);
        var rb = Find(b);
        return panelA != panelB && ra != rb && !PanelsOf(ra).Contains(panelB) && !PanelsOf(rb).Contains(panelA);
    }

    private HashSet<Guid> PanelsOf(Guid root) => panels.TryGetValue(root, out var set) ? set : [];

    private Guid Find(Guid id)
    {
        var root = id;
        while (parent.TryGetValue(root, out var next) && next != root)
        {
            root = next;
        }

        parent[id] = root;
        return root;
    }
}
