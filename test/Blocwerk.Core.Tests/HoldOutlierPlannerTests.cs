using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="HoldOutlierPlanner"/> on a synthetic two-photo wall. One facet "0" (4000 × 3000 mm) and a side facet "1".
/// Photo A sees the facet as a = 4000·x, b = 3000·(1 − y); photo B sees the left half of it from elsewhere, so the same
/// hold is at x_B = 2·x_A − 0.1 (a hold at a mm is at x_B = a / 2000 − 0.1).
/// </summary>
public class HoldOutlierPlannerTests
{
    private static readonly Guid PanelA = Guid.Parse("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid PanelB = Guid.Parse("00000000-0000-0000-0000-0000000000b0");

    private static readonly Dictionary<string, PlaneRectMm> Extents = new()
    {
        ["0"] = new PlaneRectMm(0, 4000, 0, 3000),
        ["1"] = new PlaneRectMm(0, 2000, 0, 3000),
    };

    [Fact]
    public void ConsistentPhoto_HasNothingToFix()
    {
        var holds = GridA();

        var plan = HoldOutlierPlanner.Plan(holds, [], Extents);

        Assert.Empty(plan.Fixes);
        Assert.Equal(holds.Count, plan.Panels.Single().Checked);
    }

    [Fact]
    public void HoldFarFromItsNeighbours_IsPlacedWhereTheyPutIt()
    {
        var holds = GridA();
        var stray = holds[12];
        stray.PlaneAMm += 400;
        stray.PlaneBMm -= 250;

        var fix = Assert.Single(HoldOutlierPlanner.Plan(holds, [], Extents).Fixes);

        Assert.Same(stray, fix.Hold);
        Assert.Null(fix.Twin);
        Assert.Equal(("0", 4000 * stray.X, 3000 * (1 - stray.Y)), (fix.FacetId, fix.PlaneAMm, fix.PlaneBMm), Tolerance(1));
        Assert.Equal(HoldMetric.Consensus, fix.Size!.MetricSource);
    }

    [Fact]
    public void HoldOnTheWrongFacet_AmongNeighboursOnOne_IsMovedToIt()
    {
        var holds = GridA();
        var stray = holds[7];
        stray.FacetId = "1";

        var fix = Assert.Single(HoldOutlierPlanner.Plan(holds, [], Extents).Fixes);

        Assert.Equal(("0", stray.PlaneAMm!.Value, stray.PlaneBMm!.Value), (fix.FacetId, fix.PlaneAMm, fix.PlaneBMm), Tolerance(1));
    }

    [Fact]
    public void SmallScatter_IsLeftAlone()
    {
        var holds = GridA();
        holds[5].PlaneAMm += 60;
        holds[9].PlaneBMm -= 45;

        Assert.Empty(HoldOutlierPlanner.Plan(holds, [], Extents).Fixes);
    }

    [Fact]
    public void LessConsistentPhoto_TakesItsLinkedHoldsPositions_AndPlacesItsOthersFromThem()
    {
        var a = GridA();
        var (b, links) = PhotoBOf(a.Where(h => h.PlaneAMm < 1900).ToList());

        // Photo B placed everything 600 mm too far right (with some scatter) and two holds on the side facet; only b[1] is
        // within the twin tolerance of its copy.
        var unlinked = Hold(PanelB, 0.55, 0.45, "0", 2000 * 0.65, 3000 * 0.55);
        foreach (var (h, i) in b.Append(unlinked).Select((h, i) => (h, i)))
        {
            h.PlaneAMm += h == b[1] ? 30 : 600;
            h.PlaneBMm += h == b[1] ? 0 : (i % 2 == 0 ? 70 : -70);
        }

        (b[2].FacetId, b[4].FacetId) = ("1", "1");
        b.Add(unlinked);

        var plan = HoldOutlierPlanner.Plan([.. a, .. b], links, Extents);

        Assert.Equal([PanelA, PanelB], plan.Panels.Select(p => p.PanelId));
        Assert.All(plan.Fixes, f => Assert.Equal(PanelB, f.Hold.WallPanelId));
        var byHold = plan.Fixes.ToDictionary(f => f.Hold.Id);
        Assert.False(byHold.ContainsKey(b[1].Id));
        foreach (var (hold, twin) in links.Select(l => (b.Single(h => h.Id == l.HoldBId), a.Single(h => h.Id == l.HoldAId))).Where(p => p.Item1 != b[1]))
        {
            var fix = byHold[hold.Id];
            Assert.Same(twin, fix.Twin);
            Assert.Equal((twin.FacetId, twin.PlaneAMm!.Value, twin.PlaneBMm!.Value), (fix.FacetId, fix.PlaneAMm, fix.PlaneBMm));
        }

        var placed = byHold[unlinked.Id];
        Assert.Null(placed.Twin);
        Assert.Equal(("0", 1300.0, 1650.0), (placed.FacetId, placed.PlaneAMm, placed.PlaneBMm), Tolerance(1));
    }

    [Fact]
    public void NeighboursSplitBetweenFacets_LeaveTheHoldUndecided()
    {
        var holds = new List<Hold>();
        for (var i = 0; i < 8; i++)
        {
            var x = 0.1 + (0.1 * i);
            holds.Add(Hold(PanelA, x, 0.5, i % 2 == 0 ? "0" : "1", 4000 * x, 1500));
        }

        var stray = Hold(PanelA, 0.45, 0.52, "0", 3900, 200);
        holds.Add(stray);

        var plan = HoldOutlierPlanner.Plan(holds, [], Extents);

        Assert.DoesNotContain(plan.Fixes, f => f.Hold == stray);
        Assert.True(plan.Panels.Single().Undecided > 0);
    }

    [Fact]
    public void UnplacedVirtualAndPanelLessHolds_AreNotLookedAt()
    {
        var holds = GridA();
        holds.Add(new Hold { Id = Guid.NewGuid(), WallPanelId = PanelA, X = 0.5, Y = 0.5 });
        holds.Add(Hold(PanelA, 0.5, 0.5, "0", 9999, 9999, h => h.IsVirtual = true));
        holds.Add(Hold(PanelA, 0.5, 0.5, "0", 9999, 9999, h => h.WallPanelId = null));

        var plan = HoldOutlierPlanner.Plan(holds, [], Extents);

        Assert.Empty(plan.Fixes);
        Assert.Equal(holds.Count - 3, plan.Panels.Single().Checked);
    }

    /// <summary>A 6 × 5 grid of consistently placed holds on photo A.</summary>
    private static List<Hold> GridA()
    {
        var holds = new List<Hold>();
        for (var i = 0; i < 6; i++)
        {
            for (var j = 0; j < 5; j++)
            {
                var (x, y) = (0.1 + (0.15 * i) + (0.01 * j), 0.1 + (0.18 * j));
                holds.Add(Hold(PanelA, x, y, "0", 4000 * x, 3000 * (1 - y)));
            }
        }

        return holds;
    }

    /// <summary>Photo B's copies of photo A's holds (correctly placed for now) and the links between them.</summary>
    private static (List<Hold> Holds, List<HoldLinkPair> Links) PhotoBOf(List<Hold> a)
    {
        var b = a.Select(h => Hold(PanelB, (h.PlaneAMm!.Value / 2000) - 0.1, h.Y, h.FacetId!, h.PlaneAMm.Value, h.PlaneBMm!.Value)).ToList();
        return (b, a.Zip(b, (x, y) => new HoldLinkPair(x.Id, y.Id)).ToList());
    }

    private static Hold Hold(Guid panel, double x, double y, string facet, double a, double b, Action<Hold>? configure = null)
    {
        var hold = new Hold
        {
            Id = Guid.NewGuid(), WallPanelId = panel, X = x, Y = y, Radius = 0.01, FacetId = facet, PlaneAMm = a, PlaneBMm = b,
            WidthMm = 40, HeightMm = 30, AreaMm2 = 900, MetricSource = HoldMetric.MultiMarker,
        };
        configure?.Invoke(hold);
        return hold;
    }

    private static TupleTolerance Tolerance(double mm) => new(mm);

    /// <summary>Compares (facet, a, b) tuples with a tolerance on a and b.</summary>
    private sealed class TupleTolerance(double mm) : IEqualityComparer<(string, double, double)>
    {
        public bool Equals((string, double, double) x, (string, double, double) y) =>
            x.Item1 == y.Item1 && Math.Abs(x.Item2 - y.Item2) <= mm && Math.Abs(x.Item3 - y.Item3) <= mm;

        public int GetHashCode((string, double, double) obj) => obj.Item1.GetHashCode();
    }
}
