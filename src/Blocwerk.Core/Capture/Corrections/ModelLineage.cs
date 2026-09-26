// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>One model of a wall as the lineage sees it.</summary>
/// <param name="Id">The model.</param>
/// <param name="ParentId">The model it was derived from (<see cref="Entities.WallGeometryModel.DerivedFromModelId"/>).</param>
/// <param name="IsCorrection">Whether a correction derived it, i.e. it is a known similarity of its parent.</param>
public sealed record LineageNode(Guid Id, Guid? ParentId, bool IsCorrection);

/// <summary>One step between neighbouring versions: parent → correction (down) or back (up).</summary>
/// <param name="From">The model the data is in.</param>
/// <param name="To">The model it is carried to.</param>
/// <param name="Up">True from a correction to the model it was derived from.</param>
public sealed record LineageStep(Guid From, Guid To, bool Up)
{
    /// <summary>Gets the correction of the two (whose stamp describes the step).</summary>
    public Guid Child => Up ? From : To;

    /// <summary>Gets the model it was derived from.</summary>
    public Guid Parent => Up ? To : From;
}

/// <summary>
/// How two model versions of a wall are related through corrections: only a correction is a known similarity of its
/// parent, so data can be carried between two versions exactly when a chain of corrections connects them (up to their
/// common ancestor, then down).
/// </summary>
public static class ModelLineage
{
    /// <summary>The steps from <paramref name="from"/> to <paramref name="to"/>; empty for the same model; null when no chain of corrections connects them.</summary>
    /// <param name="nodes">The wall's models by id.</param>
    /// <param name="from">The model the data is in.</param>
    /// <param name="to">The model it should be carried to.</param>
    /// <returns>The steps, in order, or null.</returns>
    public static IReadOnlyList<LineageStep>? Path(IReadOnlyDictionary<Guid, LineageNode> nodes, Guid from, Guid to)
    {
        if (from == to)
        {
            return [];
        }

        var up = Ancestry(nodes, from);
        var down = Ancestry(nodes, to);
        var i = up.FindIndex(down.Contains);
        if (i < 0)
        {
            return null;
        }

        var j = down.IndexOf(up[i]);
        var steps = new List<LineageStep>();
        for (var k = 0; k < i; k++)
        {
            steps.Add(new LineageStep(up[k], up[k + 1], Up: true));
        }

        for (var k = j - 1; k >= 0; k--)
        {
            steps.Add(new LineageStep(down[k + 1], down[k], Up: false));
        }

        return steps;
    }

    /// <summary>The model and, while it is a correction, the models it was derived from.</summary>
    private static List<Guid> Ancestry(IReadOnlyDictionary<Guid, LineageNode> nodes, Guid id)
    {
        var chain = new List<Guid> { id };
        while (chain.Count < 1000 && nodes.TryGetValue(chain[^1], out var node) && node.IsCorrection
               && node.ParentId is { } parent && nodes.ContainsKey(parent) && !chain.Contains(parent))
        {
            chain.Add(parent);
        }

        return chain;
    }
}
