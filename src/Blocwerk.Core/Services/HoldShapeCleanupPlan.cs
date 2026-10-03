using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>The planned changes of one clean-up run, with the counters its summary reports.</summary>
internal sealed class HoldShapeCleanupPlan(int photos)
{
    public List<(Hold Hold, HoldShapeChange Change)> Changes { get; } = [];

    public int AutoShapes { get; set; }

    public int Locked { get; set; }

    public HoldShapeCleanupSummary Summary(Guid? batchId)
    {
        int Count(HoldShapeChangeKind k) => Changes.Count(c => c.Change.Kind == k);
        return new HoldShapeCleanupSummary(
            batchId,
            photos,
            AutoShapes,
            Count(HoldShapeChangeKind.Smoothed),
            Count(HoldShapeChangeKind.Clipped),
            Count(HoldShapeChangeKind.Circle) + Count(HoldShapeChangeKind.ShrunkCircle) + Count(HoldShapeChangeKind.Unresolved),
            Count(HoldShapeChangeKind.ShrunkCircle),
            Count(HoldShapeChangeKind.Unresolved),
            Locked);
    }
}
