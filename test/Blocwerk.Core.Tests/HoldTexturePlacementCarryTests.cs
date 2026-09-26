using System.Text.Json;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Carrying a previous placement onto a new model: it moves through world space (a rebased facet frame gives the same
/// point other plane coordinates), and it is only kept when this run's evidence does not contradict it.
/// </summary>
public class HoldTexturePlacementCarryTests
{
    [Fact]
    public void Carry_AcrossARebasedFacetFrame_KeepsTheWorldPosition()
    {
        var s = Math.Sqrt(0.5);
        var previous = new FacetFrame([0, 0, 0], [1, 0, 0], [0, -s, s], [0, -s, -s]);

        // The same plane, its (a, b) axes rotated 3° in the plane and its origin moved 400 mm along it.
        var (c, n) = (Math.Cos(3 * Math.PI / 180), Math.Sin(3 * Math.PI / 180));
        double[] u = [c, -n * s, n * s], v = [-n, -c * s, c * s];
        var rebased = new FacetFrame(Vec3.Add([300, 0, 0], Vec3.Scale(v, 250)), u, v, [0, -s, -s]);

        var carried = PlacementCarrier.Carry(previous, rebased, new PlaneRectMm(-500, 6000, -500, 4000), 2000, 700);

        Assert.NotNull(carried);
        var world = previous.ToWorld(2000, 700);
        Assert.True(Vec3.Distance(world, rebased.ToWorld(carried.Value.A, carried.Value.B)) < 1e-6);
        Assert.True(Math.Abs(carried.Value.A - 2000) + Math.Abs(carried.Value.B - 700) > 100, "raw (a, b) would be far off");
    }

    [Fact]
    public void Evidence_MeasuresTheRegistrationOfTheFacetAndTheLinkedHolds()
    {
        var frames = new Dictionary<string, FacetFrame> { ["0"] = new([0, 0, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0]) };

        // Normalised photo → facet 0: a = 4000 x, b = 3000 − 3000 y.
        var map = PlaneHomography.FromCoefficients([4000, 0, 0, 0, -3000, 3000, 0, 0, 1]);
        var registration = new FacetRegistration("0", true, 100, 90, 50, 0.5, 0.5, 1, null, map, new PlaneRectMm(0, 2000, 0, 3000));

        Assert.Null(CarryEvidence.Disagreement(0.25, 0.5, ("0", 1000, 1500), frames, [], []));
        Assert.Equal(30, CarryEvidence.Disagreement(0.25, 0.5, ("0", 1000, 1530), frames, [registration], [])!.Value, 6);
        Assert.Equal(200, CarryEvidence.Disagreement(0.9, 0.9, ("0", 1000, 1500), frames, [], [("0", 1000, 1700)])!.Value, 6);
        Assert.False(CarryEvidence.Allows(CarryEvidence.Disagreement(0.25, 0.9, ("0", 1000, 1500), frames, [registration], [])));
    }

    [Fact]
    public async Task ACarryTheReRegisteredPhotoContradicts_IsNotKept_AndTheHoldIsNotMeasured()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var right = await s.AddHoldAsync(0.75, 0.2);
        var wrong = await s.AddHoldAsync(0.8, 0.5);
        var service = s.Service();
        var first = await service.PlaceAsync(h.WallId);

        // The first model's run put `wrong` onto facet 0 (it is on facet 1 at a ≈ 1200, b = 1500).
        await MisplaceAsync(h, first.RunId, wrong, "0", 1500, 1500);

        // The re-capture: photo c0 registers facet 0 again (its frame moved 10 mm along u), but not facet 1.
        var model = await HoldTexturePlacementRecoveryTests.RecaptureAsync(h, s);
        s.Matcher.Views.Add(new FakeTextureView(10, 2, 0, 2000, 100, HoldPlacementScenario.Shift(99.5 - 10)));
        var result = await service.PlaceFromPipelineAsync(h.WallId, model, h.Owner.Id);

        var c0 = result!.Panels.Single(p => p.PanelId == s.PanelC0);
        Assert.Equal((1, 1, 1), (c0.Placed, c0.Carried, c0.Failed));
        var holds = await s.LoadHoldsAsync();
        Assert.Equal(("1", HoldMetric.TextureRegistrationCarried), (holds[right].FacetId, holds[right].MetricSource));
        Assert.Equal(1000, holds[right].PlaneAMm!.Value, 3);
        Assert.Null(holds[wrong].FacetId);
        Assert.Null(holds[wrong].PlaneAMm);
        Assert.Null(holds[wrong].MetricSource);

        await service.RevertAsync(h.WallId, result.RunId);
        var after = await s.LoadHoldsAsync();
        Assert.Equal(("0", 1500.0), (after[wrong].FacetId, after[wrong].PlaneAMm));
    }

    /// <summary>Rewrites a hold's placement as if <paramref name="runId"/> had written it there.</summary>
    private static async Task MisplaceAsync(WallTestHarness h, Guid runId, Guid holdId, string facet, double a, double b)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        (hold.FacetId, hold.PlaneAMm, hold.PlaneBMm) = (facet, a, b);
        var run = await db.HoldPlacementRuns.SingleAsync(r => r.Id == runId);
        var entries = HoldPlacementEntry.FromJson(run.HoldsJson)
            .Select(e => e.HoldId == holdId ? e with { PlacementHash = HoldPlacementEntry.HashPlacement(hold) } : e);
        run.HoldsJson = JsonSerializer.Serialize(entries);
        await db.SaveChangesAsync();
    }
}
