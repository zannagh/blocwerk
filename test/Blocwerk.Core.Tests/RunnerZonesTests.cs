// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>The optional <c>zones.json</c> of a training bundle (the wall zones gsplat trains with): known shape only.</summary>
public class RunnerZonesTests
{
    /// <summary>What the splat worker writes (zones.spec + toWorldMm), abridged to one facet.</summary>
    private const string Zones =
        """
        {"version":1,"facets":[{"id":"12","o":[0,0,0],"u":[1,0,0],"v":[0,0,1],"n":[0,-1,0],"ext":[0,2000,0,1000]}],
         "floorMm":0.0,"boxLo":[-400.0,-400.0,-150.0],"boxHi":[2400.0,400.0,1600.0],
         "params":{"slab_margin_mm":100.0,"air_outside":true,"surround_share":0.1},
         "toWorldMm":[[250,0,0,1],[0,250,0,2],[0,0,250,3],[0,0,0,1]]}
        """;

    [Fact]
    public void Bundle_KeepsAValidZonesFile()
    {
        var (bytes, _) = RunnerBundle.Sanitize(RunnerFixture.Bundle((RunnerZones.FileName, Encoding.UTF8.GetBytes(Zones))));

        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == RunnerZones.FileName);
    }

    [Theory]
    [InlineData("""{"facets":[],"boxLo":[0,0,0],"boxHi":[1,1,1],"params":{},"toWorldMm":[[1,0,0,0],[0,1,0,0],[0,0,1,0],[0,0,0,1]]}""")]
    [InlineData("""{"facets":[{"o":[0,0,0],"u":[1,0,0],"v":[0,0,1],"n":[0,-1,0],"ext":[0,1,0,1],"image":"IMG_1.jpg"}],"boxLo":[0,0,0],"boxHi":[1,1,1],"params":{},"toWorldMm":[[1,0,0,0],[0,1,0,0],[0,0,1,0],[0,0,0,1]]}""")]
    [InlineData("""{"facets":[{"o":[0,0,0],"u":[1,0,0],"v":[0,0,1],"n":[0,-1,0],"ext":[0,1,0,1]}],"boxLo":[0,0,0],"boxHi":[1,1,1],"params":{"note":"text"},"toWorldMm":[[1,0,0,0],[0,1,0,0],[0,0,1,0],[0,0,0,1]]}""")]
    [InlineData("""{"facets":[{"o":[0,0,0],"u":[1,0,0],"v":[0,0,1],"n":[0,-1,0],"ext":[0,1,0,1]}],"boxLo":[0,0,0],"boxHi":[1,1,1],"params":{},"toWorldMm":[[1,0,0,0]]}""")]
    [InlineData("""{"facets":[{"o":[0,0,0],"u":[1,0,0],"v":[0,0,1],"n":[0,-1,0],"ext":[0,1,0,1]}],"boxLo":[0,0,0],"boxHi":[1,1,1],"params":{},"toWorldMm":[[1,0,0,0],[0,1,0,0],[0,0,1,0],[0,0,0,1]],"cameras":[]}""")]
    [InlineData("not json")]
    public void Bundle_WithABadZonesFile_IsRefused(string zones)
    {
        Assert.Throws<InvalidDataException>(
            () => RunnerBundle.Sanitize(RunnerFixture.Bundle((RunnerZones.FileName, Encoding.UTF8.GetBytes(zones)))));
    }
}
