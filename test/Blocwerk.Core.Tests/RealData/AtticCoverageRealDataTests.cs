// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests.RealData;

/// <summary>
/// The coverage analyzer's line-of-sight rule on The Attic's real model and its 356 solved cameras. A far facet whose
/// plane merely passes behind the main wall (the "why is it there" board at x = 5.1 m, the closing pieces) used to blank
/// most of the wall: only a facet's real region between camera and cell may hide it.
/// </summary>
public sealed class AtticCoverageRealDataTests
{
    private const double CellMm = 100;

    private static readonly Lazy<IReadOnlyList<CoverageCamera>> Cameras = new(() => CoverageCamera.FromModel(AtticRealData.ModelJson));

    private static readonly Lazy<Dictionary<string, CoverageOccluder>> Occluders = new(() =>
        CoverageOccluderSeams.Build(CaptureCoverageAnalyzer.Facets(AtticRealData.Model, new Dictionary<string, PlaneRectMm>()))
            .ToDictionary(o => o.Facet.Id));

    [Fact]
    public void AllThreeHundredFiftySixCamerasAreUsable()
    {
        Assert.Equal(356, Cameras.Value.Count);
        Assert.Equal(8, Occluders.Value.Count);
    }

    [Fact]
    public void FarFacets_BlockNoneOfTheMainWallsViews()
    {
        var blocked = BlockedViewsOfTheMainWall();

        // The leftover bit (coplanar, clipped at the midline), the far board and the corner piece never stand between a
        // camera and a main wall cell; before the real-region rule the far board alone hid most of the wall.
        Assert.Equal(0, blocked["2"]);
        Assert.Equal(0, blocked["4"]);
        Assert.Equal(0, blocked["6"]);

        // Nor does the kickboard, whose region ends at its seam with the main wall.
        Assert.Equal(0, blocked["3"]);
    }

    [Fact]
    public void TheSideAndClosingPieces_HideALimitedShareOfTheMainWallsViews()
    {
        var blocked = BlockedViewsOfTheMainWall();
        var all = Facing();

        // Real occlusion exists (cameras that look through the side wall or a closing piece), but it is a minority.
        Assert.InRange(blocked.Values.Sum() / (double)all, 0.04, 0.25);
        var counts = string.Join(", ", blocked.OrderBy(b => b.Key).Select(b => $"{b.Key}: {b.Value}"));
        Assert.All(new[] { "1", "5a", "7a" }, id => Assert.True(blocked[id] > 0, counts));
    }

    [Fact]
    public void TheMainWallCoverageReport_SeesAlmostEveryCellFromSomewhere()
    {
        var inputs = new CoverageInputs(
            Guid.Empty, Guid.Empty, AtticRealData.Model, Cameras.Value, [], [], new Dictionary<string, PlaneRectMm>(), new CoverageVideoInput(false, 0, null));

        var report = CaptureCoverageAnalyzer.Analyze(inputs, DateTimeOffset.UnixEpoch);

        var main = report.Facets.Single(f => f.FacetId == "0");
        var rated = main.Cells.Count(c => c != '.');
        var never = main.Cells.Count(c => c == 'n');
        Assert.Equal(356, report.PhotoViews);
        Assert.True(rated > 0.9 * main.Cells.Length, $"{rated} of {main.Cells.Length} cells rated");
        Assert.True(never < 0.05 * rated, $"{never} of {rated} cells never seen");
        Assert.True(main.Cells.Count(c => c == 'g') > 0.5 * rated, "most cells are well covered");
    }

    private static Dictionary<string, int> BlockedViewsOfTheMainWall()
    {
        var result = Occluders.Value.Keys.Where(id => id != "0").ToDictionary(id => id, _ => 0);
        foreach (var (target, camera) in FacingPairs())
        {
            foreach (var (id, occluder) in Occluders.Value.Where(o => o.Key != "0"))
            {
                if (occluder.Blocks(camera, target))
                {
                    result[id]++;
                }
            }
        }

        return result;
    }

    private static int Facing() => FacingPairs().Count();

    private static IEnumerable<(double[] Target, double[] Camera)> FacingPairs()
    {
        var frame = Occluders.Value["0"].Facet.Frame;
        var region = Occluders.Value["0"].Facet.Region;
        for (var a = region.AMin + (CellMm / 2); a < region.AMax; a += CellMm)
        {
            for (var b = region.BMin + (CellMm / 2); b < region.BMax; b += CellMm)
            {
                var target = frame.ToWorld(a, b);
                foreach (var camera in Cameras.Value.Where(c => GeometryKernel.Facing(target, frame.Normal, c.Centre)))
                {
                    yield return (target, camera.Centre);
                }
            }
        }
    }
}
