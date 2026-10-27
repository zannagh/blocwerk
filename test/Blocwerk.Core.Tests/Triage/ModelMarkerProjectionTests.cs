// <copyright file="ModelMarkerProjectionTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// The active model's printed markers mapped onto a staged photo through the carried holds' facet placements: a marker of
/// any id (a retired one too) on a facet with enough carried holds lands where the plane-to-photo fit puts it.
/// </summary>
public class ModelMarkerProjectionTests
{
    private const string Model = """
        {"markers":[
          {"id":17,"facet":"0","cornersPlaneMm":[[1000,1000],[1080,1000],[1080,1080],[1000,1080]]},
          {"id":3,"facet":"0","synthetic":true,"cornersPlaneMm":[[0,0],[80,0],[80,80],[0,80]]},
          {"id":4,"facet":"1","cornersPlaneMm":[[0,0],[80,0],[80,80],[0,80]]},
          {"id":5,"facet":"0","cornersPlaneMm":[[9000,9000],[9080,9000],[9080,9080],[9000,9080]]}
        ]}
        """;

    [Fact]
    public void Parse_SkipsSyntheticMarkers_AndUnreadableJson()
    {
        Assert.Equal(["0", "1", "0"], ModelMarkerProjection.Parse(Model).Select(m => m.FacetId));
        Assert.Empty(ModelMarkerProjection.Parse("not json"));
        Assert.Empty(ModelMarkerProjection.Parse("""{"markers":[{"facet":0,"cornersPlaneMm":[]}]}"""));
    }

    [Fact]
    public void Project_MapsAMarkerThroughItsFacetsHolds_AndSkipsThinFacetsAndOffPhotoMarkers()
    {
        // Facet 0 maps plane mm to photo px by x = 500 + 0.5·a, y = 2500 - 0.5·b; facet 1 has too few holds.
        var anchors = new List<PlaneAnchor>();
        foreach (var (a, b) in new[] { (0.0, 0.0), (2000, 0), (0, 2000), (2000, 2000), (1000, 500), (500, 1500), (1500, 1500) })
        {
            anchors.Add(new PlaneAnchor((500 + (0.5 * a)) / 4000, (2500 - (0.5 * b)) / 3000, "0", a, b));
        }

        anchors.Add(new PlaneAnchor(0.5, 0.5, "1", 0, 0));

        var quads = ModelMarkerProjection.Project(ModelMarkerProjection.Parse(Model), anchors, (4000, 3000));

        var quad = Assert.Single(quads);
        Assert.Equal(1000, quad[0].X, 6);
        Assert.Equal(2000, quad[0].Y, 6);
        Assert.Equal(1040, quad[2].X, 6);
        Assert.Equal(1960, quad[2].Y, 6);
    }
}
