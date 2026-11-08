// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Recognition.Worker.Envelope;
using Blocwerk.Recognition.Worker.Jobs;
using Blocwerk.Recognition.Worker.Protocol;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>The worker against the runner protocol 1.2 fixtures of <c>blocwerk-contracts</c>.</summary>
public class ContractConformanceTests
{
    [Fact]
    public void TheValidDetectEnvelopeIsAccepted()
    {
        var (request, files) = RecognitionEnvelope.Validate(ContractFixtures.Element("envelope/parts/valid/recognition-detect-holds-2.json"), "detect-holds@2");

        Assert.Equal([("photos", "p01.jpg"), ("photos", "p02.jpg")], files);
        Assert.Equal(1, request.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("recognition-kind-on-splat-service.json", "markers")]
    [InlineData("recognition-nine-photos.json", "detect-holds@2")]
    [InlineData("recognition-no-request.json", "presence")]
    [InlineData("recognition-unknown-part.json", "presence")]
    public void InvalidEnvelopesAreRefused(string file, string kind)
    {
        var parts = ContractFixtures.Element($"envelope/parts/invalid/{file}");

        Assert.Throws<RecognitionInputException>(() => RecognitionEnvelope.Validate(parts, kind));
    }

    [Fact]
    public void PresenceRequestAndResultFollowTheFixtures()
    {
        var presence = ContractFixtures.Read<PresenceRequest>("jobs/recognition/presence-request/valid/two-queries.json");

        Assert.Equal(new PresenceQuery(200, 300, 190, 310, 0.5, 42), presence.Queries[1]);
        Assert.Equal(0, presence.Queries[0].RadiusPx);
        Assert.Equal(
            ContractFixtures.CanonicalFixture("jobs/recognition/presence-result/valid/score-and-null.json"),
            ContractFixtures.Canonical(PresenceHandler.ToResult([0.91, null])));
    }

    [Fact]
    public void TheResultZipCarriesTheJobStatusOfTheFixture()
    {
        var claim = ContractFixtures.Read<RunnerClaim>("runner/claim/valid/recognition-interactive.json") with { Kind = "presence" };
        var at = DateTimeOffset.Parse("2026-11-08T10:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);

        using var zip = new ZipArchive(new MemoryStream(EnvelopeResult.Zip(claim, PresenceHandler.ToResult([0.91, null]), at, at.AddSeconds(2))));

        var status = JsonNode.Parse(zip.Entries.Single(e => e.FullName == "status.json").Open())!;
        var expected = JsonNode.Parse(ContractFixtures.Text("worker/job-status/valid/recognition-presence-succeeded.json"))!;
        Assert.Equal(ContractFixtures.Canonical(expected), ContractFixtures.Canonical(status));
        Assert.Equal((30, 520), (claim.LeaseSeconds, claim.RequiredMemoryMb));
    }

    [Fact]
    public void HelloAnswersParse()
    {
        Assert.Contains("detect-holds@2", ContractFixtures.Read<RunnerHelloAnswer>("runner/hello-response/valid/recognition-server.json").Capabilities!);
        Assert.Null(ContractFixtures.Read<RunnerHelloAnswer>("runner/hello-response/valid/current-server.json").Capabilities);
    }

    [Fact]
    public void PhotosAboveOneHundredMegapixelsAreRefusedBeforeDecoding()
    {
        var bundle = TestBundles.Envelope("markers", new { version = 1 }, ("photo", "big.png", TestBundles.PngHeader(10_001, 10_000)));
        var envelope = RecognitionEnvelope.Read(bundle, "markers");

        var refused = Assert.Throws<RecognitionInputException>(() => envelope.Image("photo"));

        Assert.Equal(RunnerFailure.Resource, refused.FailureKind);
    }
}
