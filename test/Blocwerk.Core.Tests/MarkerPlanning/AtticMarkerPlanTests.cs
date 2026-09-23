// <copyright file="AtticMarkerPlanTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// The owner's real wall: a plan built from its solved geometry matches the hand-built one, and the
/// PDF renders. Set BLOCWERK_PLANNER_SAMPLES to a directory to also write the sample JSON + PDFs.
/// </summary>
public class AtticMarkerPlanTests
{
    private static readonly string GeometryPath = Path.Combine(AppContext.BaseDirectory, "MarkerPlanning", "attic-wall-geometry.json");

    [Fact]
    public void PlanFromSolvedGeometry_PutsEveryIdOnTheSameSurface_AsTheHandBuiltPlan()
    {
        var plan = MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(File.ReadAllText(GeometryPath)), AtticMarkerPlan.Photo);

        Assert.Equal([0, 1, 2, 5], plan.Segments.Select(s => s.Index));
        Assert.Equal(
            AtticMarkerPlan.Plan.Markers.Select(m => (m.Id, m.Segment)).Order(),
            plan.Markers.Select(m => (m.Id, m.Segment)).Order());
        Assert.All(plan.Markers, m => Assert.Equal(125, m.SizeMm));
        Assert.Equal(45.4, plan.Segments[0].OverhangDeg, 1);
        Assert.Equal(89.0, plan.Segments[2].YawDeg, 1);
    }

    [Fact]
    public void PlanFromSolvedGeometry_StitchesANet_KickboardBelow_PiecesBesideTheMainWall()
    {
        var plan = MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(File.ReadAllText(GeometryPath)), AtticMarkerPlan.Photo);

        var byIndex = plan.Segments.ToDictionary(s => s.Index);
        Assert.Null(byIndex[0].AttachedTo);
        Assert.Equal((0, SegmentEdge.Bottom, SegmentEdge.Top), Edge(byIndex[1]));
        Assert.Equal((0, SegmentEdge.Right, SegmentEdge.Left), Edge(byIndex[5]));

        // The side triangle touches both the main wall (hypotenuse) and the kickboard's end (vertical
        // leg, below the seam). As a bounding rectangle, hanging it off the kickboard would overlap the
        // main wall in the net, so it goes beside the main wall — either is a valid unfolding.
        Assert.Contains(byIndex[2].AttachedTo!.ParentIndex, new[] { 0, 1 });
        Assert.DoesNotContain(MarkerPlanValidator.Validate(plan), i => i.Severity == PlanIssueSeverity.Error);
    }

    [Fact]
    public void Pdf_HasOverviewTableAndOneMarkerPagePerMarker()
    {
        var pdf = MarkerPlanPdf.Render(AtticMarkerPlan.Plan, "The Attic");

        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        var expected = MarkerPlanPdf.PageCount(AtticMarkerPlan.Plan);
        Assert.Equal(2 + AtticMarkerPlan.Plan.Markers.Count, expected);
        var text = Encoding.Latin1.GetString(pdf);
        Assert.Equal(expected, text.Split("/Type /Page\n").Length - 1 + (text.Split("/Type /Page ").Length - 1) + (text.Split("/Type /Page>").Length - 1));

        // Every page is A4 (210 × 297 mm = 595.3 × 841.9 pt): the markers are drawn in mm on it.
        Assert.Equal(expected, text.Split("/MediaBox [0 0 595.2").Length - 1);
    }

    [Fact]
    public void WriteSamples_WhenAsked()
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_PLANNER_SAMPLES");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var suggested = MarkerGenerator.Generate(AtticMarkerPlan.Blank, MarkerGenerationOptions.Default);
        var imported = MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(File.ReadAllText(GeometryPath)), AtticMarkerPlan.Photo);
        foreach (var (name, plan) in new[] { ("attic-plan", AtticMarkerPlan.Plan), ("attic-suggested", suggested), ("attic-from-geometry", imported) })
        {
            File.WriteAllText(Path.Combine(dir, name + ".json"), MarkerPlanJson.ToJson(plan));
            File.WriteAllBytes(Path.Combine(dir, name + ".pdf"), MarkerPlanPdf.Render(plan, "The Attic"));
            var issues = MarkerPlanValidator.Validate(plan).Select(i => $"{i.Severity} {i.Code} seg={i.Segment} id={i.MarkerId}: {i.Message}");
            File.WriteAllLines(Path.Combine(dir, name + ".issues.txt"), issues);
            File.WriteAllBytes(Path.Combine(dir, name + ".page1.png"), MarkerPlanPdf.RasterizePage(plan, "The Attic", 0, 100));
            File.WriteAllBytes(Path.Combine(dir, name + ".page3.png"), MarkerPlanPdf.RasterizePage(plan, "The Attic", 2, 100));
        }
    }

    private static (int Parent, SegmentEdge ParentEdge, SegmentEdge OwnEdge) Edge(PlanSegment s) =>
        (s.AttachedTo!.ParentIndex, s.AttachedTo.ParentEdge, s.AttachedTo.OwnEdge);
}
