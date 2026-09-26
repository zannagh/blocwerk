// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Geometry.Corrections;

namespace Blocwerk.Core.Tests;

/// <summary>Which versions a carry connects (<see cref="ModelLineage"/>) and the similarity of one correction step (<see cref="CorrectionEdge"/>).</summary>
public class CorrectionLineageTests
{
    private static readonly Guid Capture = Guid.NewGuid();
    private static readonly Guid Scaled = Guid.NewGuid();
    private static readonly Guid Turned = Guid.NewGuid();
    private static readonly Guid Sibling = Guid.NewGuid();
    private static readonly Guid Recapture = Guid.NewGuid();

    private static readonly Dictionary<Guid, LineageNode> Nodes = new()
    {
        [Capture] = new(Capture, null, false),
        [Scaled] = new(Scaled, Capture, true),
        [Turned] = new(Turned, Scaled, true),
        [Sibling] = new(Sibling, Capture, true),
        [Recapture] = new(Recapture, Capture, false),
    };

    [Fact]
    public void Path_GoesUpToTheCommonVersion_ThenDown_AndOnlyAlongCorrections()
    {
        Assert.Empty(ModelLineage.Path(Nodes, Scaled, Scaled)!);
        Assert.Equal([new LineageStep(Capture, Scaled, false), new LineageStep(Scaled, Turned, false)], ModelLineage.Path(Nodes, Capture, Turned));
        Assert.Equal([new LineageStep(Turned, Scaled, true), new LineageStep(Scaled, Capture, true)], ModelLineage.Path(Nodes, Turned, Capture));
        Assert.Equal(
            [new LineageStep(Turned, Scaled, true), new LineageStep(Scaled, Capture, true), new LineageStep(Capture, Sibling, false)],
            ModelLineage.Path(Nodes, Turned, Sibling));
        Assert.Null(ModelLineage.Path(Nodes, Recapture, Capture));
        Assert.Null(ModelLineage.Path(Nodes, Scaled, Recapture));
    }

    [Fact]
    public void Inverse_UndoesTheSimilarity()
    {
        var t = GeometrySimilarity.RotationBetween([0, 0.1, 1], [0, 0, 1], [300, 0, 500]) with { Scale = 1.0027 };
        var p = new[] { 1234.5, -20, 3000 };

        WallGeometryModelTransformerTests.AssertClose(p, t.Inverse().Apply(t.Apply(p)), 1e-9);
    }

    [Fact]
    public void Edge_ReadsTheStampedSimilarity_AndDerivesItForAnOlderStamp()
    {
        var parent = MarkerlessFixture.FeatureDoc(anchored: false);
        var t = GeometrySimilarity.RotationBetween([0, 0.05, 1], [0, 0, 1], [500, 0, 800]) with { Scale = 1.01 };
        var stamp = new JsonObject { ["kind"] = "vertical", ["scale"] = t.Scale, ["similarity"] = CorrectionEdge.ToJson(t) };
        var child = WallGeometryModelTransformer.Stamp(WallGeometryModelTransformer.TransformDocument(parent, t), stamp);

        var stored = CorrectionEdge.Of(child, parent)!;
        var older = JsonNode.Parse(child)!;
        older["quality"]!["correction"]!.AsObject().Remove("similarity");
        var derived = CorrectionEdge.Of(older.ToJsonString(), parent)!;

        Assert.Equal(t.Translation, stored.Transform.Translation);
        var p = new[] { 2000.0, 0, 1500 };
        WallGeometryModelTransformerTests.AssertClose(t.Apply(p), derived.Transform.Apply(p), 0.05);
        Assert.Null(derived.DroppedFacet);
        Assert.Null(CorrectionEdge.Of(parent, parent));
    }

    [Fact]
    public void Edge_OfADrop_IsTheIdentityWithTheFacet()
    {
        var parent = MarkerlessFixture.FeatureDoc(anchored: false);
        var child = WallGeometryModelTransformer.Stamp(
            WallGeometryModelTransformer.DropFacet(parent, "1")!, new JsonObject { ["kind"] = "drop", ["facet"] = "1", ["scale"] = 1.0 });

        var edge = CorrectionEdge.Of(child, parent)!;

        Assert.Equal("1", edge.DroppedFacet);
        Assert.Equal(1, edge.Transform.Scale);
    }
}
