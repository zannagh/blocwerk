// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Recognition.Worker.Envelope;
using Blocwerk.Recognition.Worker.Jobs;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>The outline-holds and texture-match documents against the runner protocol 1.2 fixtures.</summary>
public class OutlineTextureConformanceTests
{
    [Fact]
    public void OutlineRequestsParseIntoSeeds()
    {
        var request = ContractFixtures.Read<OutlineRequest>("jobs/recognition/outline-request/valid/two-seeds.json");

        Assert.Equal([1, 2], request.Seeds.Select(s => s.Id));
        Assert.Equal(new HoldSeed(0.42, 0.61, 0.012), request.Seeds[0].ToSeed());
        Assert.Equal(new HoldSeed(0.1, 0.2, 0.02, 0.03, 0.04), request.Seeds[1].ToSeed());
        Assert.False(request.Options!.Joint);
    }

    [Fact]
    public void OutlineResultsMatchTheFixture()
    {
        var fixture = JsonNode.Parse(ContractFixtures.Text("jobs/recognition/outline-result/valid/one-outline-one-failure.json"))!;
        fixture["outlines"]!.AsArray().RemoveAt(1); // The worker always outlines (a circle fallback at worst), so it sends no failure entry.
        var outline = new HoldOutlineResult(
            [new(0.41, 0.6), new(0.43, 0.6), new(0.43, 0.62), new(0.41, 0.62)],
            0.42,
            0.61,
            [new ShapePoint { Dx = -0.01, Dy = -0.01 }, new ShapePoint { Dx = 0.01, Dy = -0.01 }, new ShapePoint { Dx = 0.01, Dy = 0.01 }, new ShapePoint { Dx = -0.01, Dy = 0.01 }],
            5230.0,
            new HoldOutlineBounds(0.41, 0.6, 0.02, 0.02),
            0.84,
            HoldOutlineMethod.Contour,
            new HoldFingerprint
            {
                L = 120.5, A = 140.1, B = 110.9, AreaPx = 5230.0, Aspect = 1.3, OrientationDeg = 24.0, Solidity = 0.93,
                Histogram = [0.1, 0.0, 0.2, 0.0, 0.0, 0.3, 0.1, 0.0, 0.0, 0.2, 0.0, 0.1, 0.0],
                Hu = [0.2, 0.01, 0.0, 0.0, 0.0, 0.0, 0.0],
            })
        {
            Corroborated = true,
            Distinct = true,
        };

        Assert.Equal(ContractFixtures.Canonical(fixture), ContractFixtures.Canonical(OutlineHoldsHandler.ToResult([1], [outline])));
    }

    [Fact]
    public void TextureMatchRequestsParseAndShortSeedsAreRefused()
    {
        var request = ContractFixtures.Read<TextureMatchRequest>("jobs/recognition/texture-match-request/valid/seeded-attempt.json");
        var shortSeed = ContractFixtures.Read<TextureMatchRequest>("jobs/recognition/texture-match-request/invalid/short-seed.json");

        Assert.Equal(new[] { 1.0, 0, 0, 0, 1, 0, 0, 0, 1 }, request.CheckedSeed());
        Assert.Equal(new PhotoTextureAttempt(7, null, 1.05), request.Attempt);
        Assert.Equal(2.0, request.TextureMmPerPx);
        Assert.Throws<RecognitionInputException>(() => shortSeed.CheckedSeed());
    }

    [Fact]
    public void TextureMatchResultsMatchTheFixture()
    {
        var match = new PhotoTextureMatch([new PointCorrespondence(1200.5, 800.25, 640.0, 410.5)], 57, null);

        Assert.Equal(
            ContractFixtures.CanonicalFixture("jobs/recognition/texture-match-result/valid/pairs.json"),
            ContractFixtures.Canonical(TextureMatchHandler.ToResult(match)));
    }

    [Fact]
    public void TheTextureMatchEnvelopeTakesAnOptionalMask()
    {
        var (request, files) = RecognitionEnvelope.Validate(ContractFixtures.Element("envelope/parts/valid/recognition-texture-match.json"), "texture-match");

        Assert.Equal([("photo", "photo.jpg"), ("texture", "facet_f1.jpg"), ("mask", "facet_f1_mask.png")], files);
        Assert.Equal(2.0, request.GetProperty("textureMmPerPx").GetDouble());
    }
}
