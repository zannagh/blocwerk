// <copyright file="HoldSyncReport.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>One explicit-value disagreement between linked holds and how it was settled.</summary>
/// <param name="Property">The property that disagreed (Color, Material, ...).</param>
/// <param name="KeptHoldId">The hold whose value won.</param>
/// <param name="Kept">The winning value.</param>
/// <param name="Replaced">The distinct values that were replaced by it.</param>
public sealed record HoldSyncConflict(string Property, Guid KeptHoldId, string Kept, IReadOnlyList<string> Replaced);

/// <summary>What one reconciliation changed: holds updated per property, and the conflicts it settled.</summary>
public sealed class HoldSyncReport
{
    private readonly Dictionary<string, int> perProperty = new();
    private readonly HashSet<Guid> changedHolds = [];
    private readonly List<HoldSyncConflict> conflicts = [];

    /// <summary>Gets how many holds were updated, by property name.</summary>
    public IReadOnlyDictionary<string, int> UpdatedByProperty => perProperty;

    /// <summary>Gets the explicit-value conflicts that were settled toward the winner panel.</summary>
    public IReadOnlyList<HoldSyncConflict> Conflicts => conflicts;

    /// <summary>Gets or sets how many link groups (a hold and its twins) were looked at.</summary>
    public int Groups { get; set; }

    /// <summary>Gets how many distinct holds changed.</summary>
    public int HoldsChanged => changedHolds.Count;

    /// <summary>Gets a value indicating whether anything changed.</summary>
    public bool Any => changedHolds.Count > 0;

    /// <summary>Gets the ids of the holds that changed.</summary>
    public IReadOnlyCollection<Guid> ChangedHoldIds => changedHolds;

    /// <summary>A one-line summary for logs and the settings page.</summary>
    /// <returns>The summary.</returns>
    public string Summary() =>
        $"{HoldsChanged} holds updated ({(perProperty.Count == 0 ? "none" : string.Join(", ", perProperty.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}")))}), "
        + $"{conflicts.Count} conflicts settled toward the winner panel, {Groups} link groups";

    /// <summary>Folds another report into this one.</summary>
    /// <param name="other">The report to add.</param>
    public void Add(HoldSyncReport other)
    {
        foreach (var (property, count) in other.perProperty)
        {
            perProperty[property] = perProperty.GetValueOrDefault(property) + count;
        }

        changedHolds.UnionWith(other.changedHolds);
        conflicts.AddRange(other.conflicts);
        Groups += other.Groups;
    }

    internal void Updated(string property, Guid holdId)
    {
        perProperty[property] = perProperty.GetValueOrDefault(property) + 1;
        changedHolds.Add(holdId);
    }

    internal void Conflicted(HoldSyncConflict conflict) => conflicts.Add(conflict);
}
