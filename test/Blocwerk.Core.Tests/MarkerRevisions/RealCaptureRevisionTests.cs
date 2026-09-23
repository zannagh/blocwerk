// <copyright file="RealCaptureRevisionTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Tests.MarkerPlanning;
using Xunit.Abstractions;
using static Blocwerk.Core.Tests.MarkerRevisions.RegistrationFixtures;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// The Attic's real capture through a simulated marker change: revision 1 is "Start from measured wall" on
/// the legacy model; revision 2 replaces the spares 24–27 by new ids 44–47 and shrinks fillers 4 and 5 to
/// 60 mm (same ids, new sheets somewhere else). A second real solve of the wall — three photos fewer, in a
/// deliberately different frame — is registered with only the unchanged markers and must land on the
/// original within millimetres.
/// </summary>
public class RealCaptureRevisionTests(ITestOutputHelper output)
{
    private static readonly RigidTransform3D Offset = Transform(-3, 2, [-410, 95, -35]);

    [Fact]
    public void ShrunkFillersAndNewIds_RegisterOntoTheOriginal_WithinMillimetres()
    {
        var rev1 = MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(Rev1Json), AtticMarkerPlan.Photo);
        var rev2 = RevisionTwo(rev1);
        var diff = MarkerPlanDiff.Compare(rev1.Markers, rev2.Markers);
        var solved = Displace(Displace(Move(Rev2Json, Offset, shiftFacet: "0", shiftAMm: 180), 4, 0, -90, 0.48), 5, 60, 0, 0.48);

        var result = WallFrameRegistration.Register(Doc(Rev1Json), Doc(solved), diff.UnchangedIds);
        var json = WallFrameRegistrationWriter.Rewrite(solved, Rev1Json, result, diff.UnchangedIds, new(Guid.NewGuid(), null, 2));

        Assert.True(result.Accepted, result.Message);
        Assert.Equal([4, 5], result.ChangedIds);
        Assert.DoesNotContain(4, result.UsedIds);
        Assert.Empty(WallGeometryValidator.Validate(Doc(json)));
        var original = PlaneCentres(Rev1Json);
        var registered = PlaneCentres(json);
        var errors = diff.UnchangedIds.Where(registered.ContainsKey).Select(id => Error(original[id], registered[id])).ToList();
        var renamed = new[] { 24, 25, 26, 27 }.Select(id => Error(original[id], registered[id + 20])).ToList();
        output.WriteLine($"fit rms {result.RmsMm:F2} mm, max {result.MaxMm:F2} mm over {result.UsedIds.Count} markers; outliers [{string.Join(",", result.OutlierIds)}]");
        output.WriteLine($"unchanged markers vs original: rms {Rms(errors):F2} mm, max {errors.Max():F2} mm");
        output.WriteLine($"new ids 44-47 vs the same sheets as 24-27: rms {Rms(renamed):F2} mm, max {renamed.Max():F2} mm");
        Assert.True(Rms(errors) < 8 && errors.Max() < 15, $"rms {Rms(errors)}, max {errors.Max()}");
        Assert.True(renamed.Max() < 15, $"max {renamed.Max()}");
    }

    [Fact]
    public void WithoutRegistration_TheSecondSolveIsTensOfMillimetresOff()
    {
        var original = PlaneCentres(Rev1Json);
        var raw = PlaneCentres(Rev2Json);
        var errors = original.Keys.Where(raw.ContainsKey).Select(id => Error(original[id], raw[id])).ToList();

        output.WriteLine($"raw second solve vs original: rms {Rms(errors):F2} mm, max {errors.Max():F2} mm");
        Assert.True(errors.Max() > 20);
    }

    /// <summary>Revision 2: spares 24–27 re-issued as 44–47 in place, fillers 4 and 5 shrunk to 60 mm.</summary>
    internal static MarkerPlan RevisionTwo(MarkerPlan rev1)
    {
        var markers = rev1.Markers
            .Select(m => m.Id is >= 24 and <= 27 ? m with { Id = m.Id + 20 } : m)
            .Select(m => m.Id is 4 or 5 ? m with { SizeMm = 60 } : m)
            .ToList();
        return rev1 with { Markers = markers };
    }

    private static double Error((string Facet, double A, double B) a, (string Facet, double A, double B) b) =>
        a.Facet == b.Facet ? Math.Sqrt(Math.Pow(a.A - b.A, 2) + Math.Pow(a.B - b.B, 2)) : double.PositiveInfinity;

    private static double Rms(List<double> values) => Math.Sqrt(values.Average(v => v * v));

    private static WallGeometryDocument Doc(string json) => WallGeometryDocument.Parse(json);
}
