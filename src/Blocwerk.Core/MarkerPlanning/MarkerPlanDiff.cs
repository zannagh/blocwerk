// <copyright file="MarkerPlanDiff.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Compares two marker layouts (plan revisions, or a revision against the layout a solved model
/// measured) marker by marker. A marker is UNCHANGED only when the same id sits on the same segment,
/// at the same printed size and within <see cref="MoveToleranceMm"/> of the same place; an id that is
/// reused with another size or position is a changed marker, never the old one. Only unchanged markers
/// may tie a photo or model of one revision to another.
/// </summary>
public static class MarkerPlanDiff
{
    /// <summary>A centre that moved further than this (mm, in its segment frame) is a moved marker.</summary>
    public const double MoveToleranceMm = 10;

    /// <summary>Printed sizes further apart than this (mm) are a resized marker.</summary>
    public const double SizeToleranceMm = 0.5;

    /// <summary>Compares <paramref name="from"/> (older) with <paramref name="to"/> (newer).</summary>
    public static MarkerPlanDiffResult Compare(IEnumerable<PlanMarker> from, IEnumerable<PlanMarker> to)
    {
        var before = ById(from);
        var after = ById(to);
        var entries = new List<MarkerDiffEntry>();
        foreach (var id in before.Keys.Union(after.Keys).Order())
        {
            before.TryGetValue(id, out var old);
            after.TryGetValue(id, out var now);
            entries.Add(Entry(id, old, now));
        }

        return new MarkerPlanDiffResult(entries);
    }

    /// <summary>The ids unchanged between two layouts (see <see cref="Compare"/>).</summary>
    public static IReadOnlySet<int> UnchangedIds(IEnumerable<PlanMarker> from, IEnumerable<PlanMarker> to) =>
        Compare(from, to).UnchangedIds;

    private static MarkerDiffEntry Entry(int id, PlanMarker? old, PlanMarker? now)
    {
        if (old is null)
        {
            return new MarkerDiffEntry(id, MarkerChange.Added, null, now, null);
        }

        if (now is null)
        {
            return new MarkerDiffEntry(id, MarkerChange.Removed, old, null, null);
        }

        var change = MarkerChange.None;
        double? moved = null;
        if (old.Segment != now.Segment)
        {
            change |= MarkerChange.Reassigned;
        }
        else
        {
            moved = Math.Sqrt(Math.Pow(now.XMm - old.XMm, 2) + Math.Pow(now.YMm - old.YMm, 2));
            if (moved > MoveToleranceMm)
            {
                change |= MarkerChange.Moved;
            }
        }

        if (Math.Abs(now.SizeMm - old.SizeMm) > SizeToleranceMm)
        {
            change |= MarkerChange.Resized;
        }

        return new MarkerDiffEntry(id, change, old, now, moved);
    }

    private static Dictionary<int, PlanMarker> ById(IEnumerable<PlanMarker> markers)
    {
        var map = new Dictionary<int, PlanMarker>();
        foreach (var marker in markers)
        {
            map.TryAdd(marker.Id, marker);
        }

        return map;
    }
}

/// <summary>How one marker id differs between two layouts. Moved, resized and reassigned combine.</summary>
[Flags]
public enum MarkerChange
{
    /// <summary>Same segment, size and place.</summary>
    None = 0,

    /// <summary>Same segment, but the centre moved more than <see cref="MarkerPlanDiff.MoveToleranceMm"/>.</summary>
    Moved = 1,

    /// <summary>Printed at another size.</summary>
    Resized = 2,

    /// <summary>Now on another segment.</summary>
    Reassigned = 4,

    /// <summary>Only in the newer layout.</summary>
    Added = 8,

    /// <summary>Only in the older layout.</summary>
    Removed = 16,
}

/// <summary>One id's comparison.</summary>
/// <param name="Id">The marker id.</param>
/// <param name="Change">What changed (<see cref="MarkerChange.None"/> = unchanged).</param>
/// <param name="Before">The marker in the older layout, if any.</param>
/// <param name="After">The marker in the newer layout, if any.</param>
/// <param name="MovedMm">Centre distance when both sit on the same segment.</param>
public sealed record MarkerDiffEntry(int Id, MarkerChange Change, PlanMarker? Before, PlanMarker? After, double? MovedMm)
{
    /// <summary>True when the id means the same physical marker in both layouts.</summary>
    public bool IsUnchanged => Change == MarkerChange.None;
}

/// <summary>A whole comparison, with the per-kind id lists the planner shows.</summary>
/// <param name="Entries">Every id of either layout, ascending.</param>
public sealed record MarkerPlanDiffResult(IReadOnlyList<MarkerDiffEntry> Entries)
{
    /// <summary>Ids that mean the same physical marker in both layouts.</summary>
    public IReadOnlySet<int> UnchangedIds { get; } = Entries.Where(e => e.IsUnchanged).Select(e => e.Id).ToHashSet();

    /// <summary>Ids only in the newer layout.</summary>
    public IReadOnlyList<int> Added => Ids(MarkerChange.Added);

    /// <summary>Ids only in the older layout.</summary>
    public IReadOnlyList<int> Removed => Ids(MarkerChange.Removed);

    /// <summary>Ids that moved on their segment.</summary>
    public IReadOnlyList<int> Moved => Ids(MarkerChange.Moved);

    /// <summary>Ids printed at another size.</summary>
    public IReadOnlyList<int> Resized => Ids(MarkerChange.Resized);

    /// <summary>Ids now on another segment.</summary>
    public IReadOnlyList<int> Reassigned => Ids(MarkerChange.Reassigned);

    /// <summary>Ids that need a new print or a new place: added, moved, resized or reassigned.</summary>
    public IReadOnlyList<int> ToPrint => Entries
        .Where(e => e.Change != MarkerChange.None && !e.Change.HasFlag(MarkerChange.Removed))
        .Select(e => e.Id)
        .ToList();

    /// <summary>True when nothing changed.</summary>
    public bool IsEmpty => Entries.All(e => e.IsUnchanged);

    private List<int> Ids(MarkerChange kind) => Entries.Where(e => e.Change.HasFlag(kind)).Select(e => e.Id).ToList();
}
