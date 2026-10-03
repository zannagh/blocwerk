// <copyright file="CaptureGeometryOverrideTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall admin's override of the measuring path at the start: "features" reconstructs from photo features even when
/// markers are present (refused when the services can't), "markers" never falls back to features.
/// </summary>
public class CaptureGeometryOverrideTests
{
    [Fact]
    public async Task ForcedFeatures_OnAMarkerWall_RunsTheFeaturePath()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector());
        s.Client.GeometryJson = MarkerlessFixture.FeatureDoc(anchored: false, gravityKnown: false);
        var draftId = await DraftAsync(s, glyphs: true);

        Assert.Empty(await s.Service.StartAsync(
            draftId, new CaptureDeclarations([], []), null, SplatQuality.High, CaptureGeometryOverride.Features));
        await s.Processor.ProcessAsync(draftId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureGeometryMode.Features, capture.GeometryMode);
        Assert.NotNull(capture.SfmJobId);
        Assert.Empty(s.Client.JsonSubmissions);
        Assert.Contains(s.Client.MultipartSubmissions, m => m.Kind == MarkerlessCaptureSupport.SolveKind);
        Assert.Equal(WallGeometryFrameSource.Features, (await db.WallGeometryModels.SingleAsync()).FrameSource);
    }

    [Fact]
    public async Task ForcedFeatures_WithoutTheSfmServices_IsRefused_AndNothingStarts()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector(), sfm: false);
        var draftId = await DraftAsync(s, glyphs: true);

        var problems = await s.Service.StartAsync(
            draftId, new CaptureDeclarations([], []), null, SplatQuality.High, CaptureGeometryOverride.Features);

        Assert.Contains("Measuring the wall from photo features is not available on this server.", problems);
        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Draft, (await db.WallCaptures.SingleAsync()).Status);
    }

    [Fact]
    public async Task ForcedMarkers_WithoutMarkers_IsRefused_EvenWhenFeaturesAreAvailable()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector { Ids = [] });
        var draftId = await DraftAsync(s, glyphs: false);

        var problems = await s.Service.StartAsync(
            draftId, new CaptureDeclarations([], []), null, SplatQuality.High, CaptureGeometryOverride.Markers);

        Assert.Contains("At least two photos must show markers.", problems);
    }

    [Fact]
    public async Task Api_TakesTheModeAsLowercaseText_AndAnswers422WhenItCannotRun()
    {
        var request = JsonSerializer.Deserialize<CaptureStartRequest>(
            "{\"geometryMode\":\"features\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(CaptureGeometryOverride.Features, request.GeometryMode);
        Assert.Equal(CaptureGeometryOverride.Auto, new CaptureStartRequest().GeometryMode);

        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector(), sfm: false);
        var draftId = await DraftAsync(s, glyphs: true);
        var api = new WallCapturesController(s.Service, s.Options, NullLogger<WallCapturesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = ApiKeys.Personal() } },
        };

        var result = await api.Start(h.WallId, draftId, request);

        var problems = Assert.IsType<CaptureStartProblems>(Assert.IsType<UnprocessableEntityObjectResult>(result).Value);
        Assert.Contains(problems.Problems, p => p.Contains("photo features", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Api_ForcedFeatures_GivesNoMarkerMergeWarnings()
    {
        using var h = new WallTestHarness();
        using var s = MarkerlessFixture.Scenario(h, new SwitchableMarkerDetector());
        var draftId = await DraftAsync(s, glyphs: true);
        var api = new WallCapturesController(s.Service, s.Options, NullLogger<WallCapturesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = ApiKeys.Personal() } },
        };
        var spare = new CaptureSegmentDeclaration(4, "Spares", null, false);
        Assert.NotNull(CaptureDeclarationRules.MergeWarningFor(spare));

        var result = await api.Start(
            h.WallId, draftId, new CaptureStartRequest([spare], [], GeometryMode: CaptureGeometryOverride.Features));

        var started = Assert.IsType<CaptureStartResponse>(Assert.IsType<AcceptedResult>(result).Value);
        Assert.Empty(started.Warnings);
    }

    private static async Task<Guid> DraftAsync(CaptureScenario s, bool glyphs)
    {
        await s.Harness.SeedWallAsync(holdCount: 0);
        if (glyphs)
        {
            await WallGlyphSettingsTests.Service(s.Harness).SetGlyphSettingsAsync(s.Harness.WallId, true, 125);
        }

        var draft = await s.Service.CreateDraftAsync(s.Harness.WallId);
        for (var i = 0; i < 3; i++)
        {
            await s.Service.AddPhotoAsync(draft.CaptureId, $"IMG_{i}.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 3 + i)), CancellationToken.None);
        }

        return draft.CaptureId;
    }
}
