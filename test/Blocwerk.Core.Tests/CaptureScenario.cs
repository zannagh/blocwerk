using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Wires the capture service + pipeline over a <see cref="WallTestHarness"/> with a fake compute
/// worker, a temp-dir file store and a substituted push service. Every context comes from the
/// harness factories (own connection each — never a shared one).
/// </summary>
internal sealed class CaptureScenario : IDisposable
{
    private readonly string storeDir = Path.Combine(Path.GetTempPath(), "blocwerk-capture-tests", Guid.NewGuid().ToString("N"));

    public CaptureScenario(
        WallTestHarness harness, IMarkerDetectionService? detector = null, IKioskContext? kiosk = null, WallCapturePipelineOptions? options = null,
        IDeployBusyGate? busyGate = null, ICapturePhotoConverter? photoConverter = null)
    {
        Harness = harness;
        Options = options ?? Options;
        Settings = new BlocwerkSettings();
        Settings.WallImage.StoragePath = storeDir;
        Settings.GeometryService.Url = "http://geometry.test:8000";
        Settings.GeometryService.JobTimeout = TimeSpan.FromSeconds(30);
        Files = new FileSystemCaptureFileStore(Settings);
        Detector = detector ?? new FakeMarkerDetectionService(0, 1, 2, 6, 7, 12);
        MarkerPlans = new MarkerPlanService(harness.DbContextFactory, harness.CurrentUser, NullLogger<MarkerPlanService>.Instance, kiosk);
        Service = new WallCaptureService(
            harness.DbContextFactory, harness.CurrentUser, Files, Queue, new FakeComputeJobClientFactory(Client, SplatClient),
            NullLogger<WallCaptureService>.Instance, kiosk, Detector, MarkerPlans, Video, Options, busyGate, photoConverter: photoConverter);
        Processor = new WallCaptureProcessor(
            harness.RootContextFactory, Settings, new FakeComputeJobClientFactory(Client, SplatClient), Files, Push,
            NullLoggerFactory.Instance, Options, Detector, Video, busyGate);
    }

    public WallTestHarness Harness { get; }

    public BlocwerkSettings Settings { get; }

    public FakeComputeJobClient Client { get; } = new();

    /// <summary>The splat worker. Not configured by default (the photo-real stage is skipped); set IsConfigured.</summary>
    public FakeComputeJobClient SplatClient { get; } = new() { Service = ComputeServiceKind.Splat, IsConfigured = false };

    public ICaptureFileStore Files { get; }

    /// <summary>The video frame extractor: scripted, no ffmpeg (see <see cref="FakeVideoFrameExtractor"/>).</summary>
    public FakeVideoFrameExtractor Video { get; } = new();

    public IMarkerDetectionService Detector { get; }

    public MarkerPlanService MarkerPlans { get; }

    public WallCaptureQueue Queue { get; } = new();

    public IPushNotificationService Push { get; } = Substitute.For<IPushNotificationService>();

    public WallCapturePipelineOptions Options { get; set; } = new()
    {
        PollInitialDelay = TimeSpan.FromMilliseconds(1),
        PollMaxDelay = TimeSpan.FromMilliseconds(5),
        MaxTransientErrors = 2,
    };

    public WallCaptureService Service { get; }

    public WallCaptureProcessor Processor { get; }

    /// <summary>A glyph wall with a started capture of <paramref name="photos"/> distinct photos.</summary>
    public async Task<Guid> StartCaptureAsync(
        int photos = 2, string levelPairs = "14-15", IReadOnlyCollection<int>? leaveEmpty = null, Func<Guid, Task>? beforeStart = null)
    {
        await Harness.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(Harness).SetGlyphSettingsAsync(Harness.WallId, true, 125);
        var draft = await Service.CreateDraftAsync(Harness.WallId);
        for (var i = 0; i < photos; i++)
        {
            await Service.AddPhotoAsync(draft.CaptureId, $"IMG_{i}.jpg", ExifJpeg.Build(TinyJpeg(seed: i)), CancellationToken.None);
        }

        if (beforeStart is not null)
        {
            await beforeStart(draft.CaptureId);
        }

        var suggested = await Service.SuggestDeclarationsAsync(draft.CaptureId);
        var declarations = new CaptureDeclarations(
            suggested.Segments.Select(s => leaveEmpty?.Contains(s.Index) == true
                ? s with { VerticalReference = false, DeclaredAngleDeg = null }
                : s with { VerticalReference = s.Index != 0, DeclaredAngleDeg = s.Index == 0 ? 45 : 0 }).ToList(),
            CaptureDeclarationRules.ParseLevelPairs(levelPairs).Pairs);
        var problems = await Service.StartAsync(draft.CaptureId, declarations, "first capture");
        Assert.Empty(problems);
        return draft.CaptureId;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(storeDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>A small valid JPEG whose pixels differ by <paramref name="seed"/> (distinct hashes).</summary>
    public static byte[] TinyJpeg(int seed = 0)
    {
        using var bitmap = new SKBitmap(64, 48);
        bitmap.Erase(new SKColor((byte)(40 + (seed * 30)), 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>A valid geometry document whose cameras are the given photo names.</summary>
    public static string GeometryWithCameras(params string[] images)
    {
        var doc = JsonNode.Parse(GlyphGeometryJson.Build())!.AsObject();
        doc["cameras"] = new JsonArray(images.Select(i => (JsonNode?)new JsonObject { ["image"] = i }).ToArray());
        return doc.ToJsonString();
    }
}
