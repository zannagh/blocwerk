using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>The planned changes of one clean-up run, with the counters its summary reports. Holds nothing tracked, so it can be cached.</summary>
internal sealed class HoldShapeCleanupPlan(string version, int photos)
{
    private const int MaxListedOverlaps = 50;

    public string Version { get; } = version;

    public List<HoldShapeChange> Changes { get; } = [];

    public List<Guid> Unresolved { get; } = [];

    public List<HoldShapeOverlapPair> ManualOverlaps { get; } = [];

    public int ManualOverlapCount { get; set; }

    public int AutoShapes { get; set; }

    public int AutoCircles { get; set; }

    public int Locked { get; set; }

    /// <summary>Adds one panel's plan and its hold counts.</summary>
    /// <param name="holds">The panel's holds.</param>
    /// <param name="plan">Its plan.</param>
    public void Add(IReadOnlyList<Hold> holds, HoldShapePlan plan)
    {
        AutoShapes += holds.Count(HoldShapeCleanup.IsCleanable);
        AutoCircles += holds.Count(h => HoldShapeCleanup.IsResolvable(h) && !HoldShapeCleanup.IsCleanable(h));
        Locked += holds.Count(h => !HoldShapeCleanup.IsResolvable(h));
        Changes.AddRange(plan.Changes);
        Unresolved.AddRange(plan.Unresolved);
        ManualOverlapCount += plan.LockedOverlaps.Count;
        var byId = holds.ToDictionary(h => h.Id);
        foreach (var (a, b) in plan.LockedOverlaps.Take(Math.Max(0, MaxListedOverlaps - ManualOverlaps.Count)))
        {
            ManualOverlaps.Add(new HoldShapeOverlapPair(a, byId[a].Name, b, byId[b].Name, byId[a].X, byId[a].Y, byId[a].WallPanelId));
        }
    }

    public HoldShapeCleanupSummary Summary(Guid? batchId)
    {
        int Count(HoldShapeChangeKind k) => Changes.Count(c => c.Kind == k);
        return new HoldShapeCleanupSummary(
            batchId,
            Version,
            photos,
            AutoShapes,
            AutoCircles,
            Count(HoldShapeChangeKind.Smoothed),
            Count(HoldShapeChangeKind.Clipped),
            Count(HoldShapeChangeKind.Circle) + Count(HoldShapeChangeKind.Unresolved),
            Count(HoldShapeChangeKind.ShrunkCircle),
            Unresolved.Count,
            Locked,
            ManualOverlapCount,
            ManualOverlaps);
    }
}
