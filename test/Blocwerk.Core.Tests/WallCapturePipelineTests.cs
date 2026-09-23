using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture pipeline end to end against a fake compute worker: photos → markers → solve request
/// → import + activation → textures → notification, and every way it can fail.
/// </summary>
public class WallCapturePipelineTests
{
    [Fact]
    public async Task SuccessPath_ImportsAndActivatesTheModel_AndStoresTextures()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(1, capture.Progress);
        Assert.Null(capture.Error);
        var model = await db.WallGeometryModels.SingleAsync();
        Assert.True(model.IsActive);
        Assert.Equal(model.Id, capture.GeometryModelId);
        Assert.Equal(h.Owner.Id, model.CreatedByUserId);
        Assert.StartsWith("In-app capture", model.Notes);

        var textures = await db.WallGeometryTextures.OrderBy(t => t.FacetId).ToListAsync();
        Assert.Equal(["0", "5a"], textures.Select(t => t.FacetId));
        Assert.Equal(-100, textures[0].AMin);
        Assert.Equal(2500, textures[0].BMax);
        Assert.Equal(900, textures[1].AMax);
        Assert.All(textures, t => Assert.NotNull(s.Files.ResolvePhysicalPath(t.StoredPath)));
        Assert.All(textures, t => Assert.True(File.Exists(s.Files.ResolvePhysicalPath(t.StoredPath))));
        await s.Push.Received(1).NotifyWallModelReadyAsync(h.WallId, h.Owner.Id);
    }

    [Fact]
    public async Task SolveRequest_OmitsRowsThatDeclareNothing()
    {
        // An empty row (no angle, not a vertical reference) must not reach the solver: a declared segment
        // is never merged, so spare filler markers would otherwise split off into a facet of their own.
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync(leaveEmpty: [2]);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var (_, json) = Assert.Single(s.Client.JsonSubmissions);
        var segments = JsonNode.Parse(json)!["segments"]!.AsArray();
        Assert.Equal([0, 1], segments.Select(x => x!["index"]!.GetValue<int>()));
    }

    [Fact]
    public async Task SolveRequest_CarriesDeclarationsLevelPairsRawSizeAndFocal()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync(levelPairs: "14-15, 8-9");

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var (kind, json) = Assert.Single(s.Client.JsonSubmissions);
        Assert.Equal("solve", kind);
        var request = JsonNode.Parse(json)!;
        Assert.Equal(125, request["markerSizeMm"]!.GetValue<double>());
        Assert.Equal("DICT_4X4_50", request["dictionary"]!.GetValue<string>());
        Assert.Equal("segment*6+role", request["idScheme"]!.GetValue<string>());
        var segments = request["segments"]!.AsArray();
        Assert.Equal([0, 1, 2], segments.Select(x => x!["index"]!.GetValue<int>()));
        Assert.Equal(45, segments[0]!["declaredAngleDeg"]!.GetValue<double>());
        Assert.False(segments[0]!["verticalReference"]!.GetValue<bool>());
        Assert.True(segments[1]!["verticalReference"]!.GetValue<bool>());
        Assert.Equal("[[14,15],[8,9]]", request["levelPairs"]!.ToJsonString());

        var photos = request["photos"]!.AsArray();
        Assert.Equal(["p01", "p02"], photos.Select(p => p!["name"]!.GetValue<string>()));
        Assert.All(photos, p =>
        {
            Assert.Equal(64, p!["width"]!.GetValue<int>());
            Assert.Equal(48, p["height"]!.GetValue<int>());
            Assert.Equal(14, p["focal35mm"]!.GetValue<double>());
            Assert.Matches("^cam-[0-9a-f]{24}$", p["cameraGroup"]!.GetValue<string>());
            Assert.Equal(6, p["markers"]!.AsArray().Count);
            Assert.Equal(4, p["markers"]![0]!["corners"]!.AsArray().Count);
            Assert.True(p["markers"]![0]!["refined"]!.GetValue<bool>());
        });
    }

    [Fact]
    public async Task PhotosSentForTextures_CarryNoMetadataAtAll()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var (kind, parts) = Assert.Single(s.Client.MultipartSubmissions);
        Assert.Equal("textures", kind);
        Assert.Equal("geometry", parts[0].Name);
        var photos = parts.Where(p => p.Name == "photos").ToList();
        Assert.Equal(["p01.jpg", "p02.jpg"], photos.Select(p => p.FileName));
        foreach (var photo in photos)
        {
            Assert.Equal(-1, photo.Content.AsSpan().IndexOf("Exif\0\0"u8));
            Assert.Equal(-1, photo.Content.AsSpan().IndexOf(ExifJpeg.GpsLatitudeBytes));
            Assert.Equal(-1, photo.Content.AsSpan().IndexOf(Encoding.ASCII.GetBytes(ExifJpeg.XmpSecret)));
            Assert.Equal(-1, photo.Content.AsSpan().IndexOf("shot at home"u8));
            Assert.Equal(-1, photo.Content.AsSpan().IndexOf("iPhone"u8));
        }
    }

    [Fact]
    public async Task SolveFailure_FailsTheCapture_WithTheServiceReason_AndActivatesNothing()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.Terminal["solve"] = new ComputeJobStatus { Status = ComputeJobStates.Failed, Error = "gravity is underdetermined" };
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains("gravity is underdetermined", capture.Error);
        Assert.Empty(await db.WallGeometryModels.ToListAsync());
        await s.Push.DidNotReceiveWithAnyArgs().NotifyWallModelReadyAsync(default, default);
    }

    [Fact]
    public async Task Unauthorized_FailsWithAKeyHint_ThatNeverContainsTheKey()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Settings.GeometryService.ApiKey = "super-secret-key";
        s.Client.SubmitError = ComputeJobErrors.FromStatus(System.Net.HttpStatusCode.Unauthorized, "start the solve job", null, false);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains("API key", capture.Error);
        Assert.DoesNotContain("super-secret-key", capture.Error);
    }

    [Fact]
    public async Task JobThatNeverFinishes_IsCancelledAfterTheTimeout_AndFailsTheCapture()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Settings.GeometryService.JobTimeout = TimeSpan.FromMilliseconds(50);
        s.Client.NeverFinish.Add("solve");
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains("took longer than", capture.Error);
        Assert.Single(s.Client.Cancelled);
    }

    [Fact]
    public async Task TextureFailure_KeepsTheActiveModel_AndSaysSo()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Client.Terminal["textures"] = new ComputeJobStatus { Status = ComputeJobStates.Failed, Error = "photo p01 is 64x48 but was solved as 48x64" };
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.SucceededWithoutTextures, capture.Status);
        Assert.Contains("is active", capture.Error);
        Assert.True((await db.WallGeometryModels.SingleAsync()).IsActive);
        Assert.Empty(await db.WallGeometryTextures.ToListAsync());
    }

    [Fact]
    public async Task NoServiceConfigured_FailsCleanly_AndStartIsRefused()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        s.Client.IsConfigured = false;

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains("not configured", capture.Error);
        Assert.False(s.Service.IsComputeConfigured);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        var problems = await s.Service.StartAsync(draft.CaptureId, Capture.CaptureDeclarations.Empty, null);
        Assert.Contains(problems, p => p.Contains("not configured"));
    }
}
