// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.View3D;

/// <summary>
/// The 3D viewer's solid wall body (<c>wall3d-body.js</c>) must never cover a real facet from a reachable camera position.
/// <c>tools/body-coverage/coverage.mjs</c> sweeps the camera over the Attic's view; this fixture is that view, kept in step
/// with <see cref="Wall3DViewBuilder"/> (set <c>BLOCWERK_UPDATE_FIXTURES=1</c> to rewrite it).
/// </summary>
public class Wall3DBodyCoverageAtticTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public void ViewFixture_MatchesTheBuilder()
    {
        var fixture = Path.Combine(RepoRoot(), "tools", "body-coverage", "attic-view.json");
        var fresh = JsonSerializer.Serialize(BuildView(), Options);
        if (Environment.GetEnvironmentVariable("BLOCWERK_UPDATE_FIXTURES") == "1")
        {
            File.WriteAllText(fixture, fresh);
        }

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fresh), JsonNode.Parse(File.ReadAllText(fixture))));
    }

    [Fact]
    public void CameraSweep_FindsNoRealFacetCoveredByTheBody()
    {
        var node = Environment.GetEnvironmentVariable("NODE") ?? "node";
        var info = new ProcessStartInfo(node, Path.Combine("tools", "body-coverage", "coverage.mjs"))
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Fail("node is required for the 3D body coverage sweep (set NODE to its path)");
            return;
        }

        using (process)
        {
            var output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
    }

    private static object BuildView()
    {
        var triangles = new Dictionary<int, PlanTriangle>
        {
            [1] = new(0, TriangleCorner.BottomLeft),
            [5] = new(2, TriangleCorner.BottomRight),
            [7] = new(6, TriangleCorner.BottomRight),
        };
        var model = WallGeometryDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "View3D", "attic-active-model.json")));
        var view = Wall3DViewBuilder.Build(new Wall { Name = "The Attic" }, model, null, planTriangles: triangles);
        return new { facets = view.Facets, recesses = view.Recesses };
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tools", "body-coverage")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with tools/body-coverage not found");
    }
}
