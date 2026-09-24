// <copyright file="CaptureQualitySettingsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture's quality knobs: each one unset keeps today's behaviour, byte for byte where it reaches a
/// worker; set, it reaches the place it tunes.
/// </summary>
public class CaptureQualitySettingsTests
{
    [Fact]
    public async Task TexturesRequest_HasNoOptionsPart_WhenNoTextureOptionIsConfigured()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var (_, parts) = Assert.Single(s.Client.MultipartSubmissions);
        Assert.Equal(["geometry", "photos", "photos"], parts.Select(p => p.Name));
    }

    [Fact]
    public async Task TexturesRequest_CarriesOnlyTheConfiguredOptions()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.Settings.GeometryTextures.MmPerPx = 1;
        s.Settings.GeometryTextures.JpegQuality = 95;
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var (_, parts) = Assert.Single(s.Client.MultipartSubmissions);
        Assert.Equal(["geometry", "options", "photos", "photos"], parts.Select(p => p.Name));
        var options = JsonNode.Parse(Encoding.UTF8.GetString(parts[1].Content))!.AsObject();
        Assert.Equal(["mmPerPx", "jpegQuality"], options.Select(o => o.Key));
        Assert.Equal(1.0, options["mmPerPx"]!.GetValue<double>());
        Assert.Equal(95, options["jpegQuality"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("1", "8192", "95", """{"mmPerPx":1,"maxSidePx":8192,"jpegQuality":95}""")]
    [InlineData("0.5", null, null, """{"mmPerPx":0.5}""")]
    [InlineData("0.1", "16384", "20", null)] // outside the worker's ranges: not sent
    [InlineData("abc", "", " ", null)]
    public void TextureSettings_BindFromConfiguration(string? mm, string? side, string? quality, string? expected)
    {
        var section = Section(new Dictionary<string, string?>
        {
            ["Blocwerk:GeometryService:Textures:MmPerPx"] = mm,
            ["Blocwerk:GeometryService:Textures:MaxSidePx"] = side,
            ["Blocwerk:GeometryService:Textures:JpegQuality"] = quality,
        });

        var settings = GeometryTextureSettings.Bind(section);

        Assert.Equal(expected, settings.ToOptionsJson());
        Assert.Equal(expected is not null, settings.IsConfigured);
    }

    [Fact]
    public void TextureSettings_AcceptOnlySidesTheAppReadsBack()
    {
        Assert.True(GeometryTextureSettings.MaxSidePxLimit <= CaptureComputeDocuments.MaxTextureSidePx);
    }

    [Fact]
    public void PipelineOptions_Defaults_KeepTodaysValues_ButTakeA48MpJpeg()
    {
        var options = WallCapturePipelineOptions.Bind(Root(new Dictionary<string, string?>()));

        Assert.Equal(40L * 1024 * 1024, options.MaxPhotoBytes);
        Assert.Equal(92, options.HeicJpegQuality);
        var request = options.VideoFrameRequest();
        Assert.Equal(new CaptureVideoFrameRequest(2.5, 120, options.VideoExtractTimeout), request);
        Assert.Equal((3, 3, 480), (request.Window, request.JpegQ, request.ScoreEdge));
    }

    [Fact]
    public void PipelineOptions_BindTheQualityKnobs_AndIgnoreValuesOutOfRange()
    {
        var options = WallCapturePipelineOptions.Bind(Root(new Dictionary<string, string?>
        {
            ["Blocwerk:Capture:MaxPhotoMb"] = "60",
            ["Blocwerk:Capture:HeicJpegQuality"] = "97",
            ["Blocwerk:Capture:FrameSharpnessWindow"] = "5",
            ["Blocwerk:Capture:FrameJpegQ"] = "2",
            ["Blocwerk:Capture:SharpnessEdge"] = "960",
        }));
        var request = options.VideoFrameRequest();
        Assert.Equal(60L * 1024 * 1024, options.MaxPhotoBytes);
        Assert.Equal(97, options.HeicJpegQuality);
        Assert.Equal((5, 2, 960), (request.Window, request.JpegQ, request.ScoreEdge));

        var bad = WallCapturePipelineOptions.Bind(Root(new Dictionary<string, string?>
        {
            ["Blocwerk:Capture:MaxPhotoMb"] = "0",
            ["Blocwerk:Capture:HeicJpegQuality"] = "101",
            ["Blocwerk:Capture:FrameJpegQ"] = "1",
        }));
        Assert.Equal((40L * 1024 * 1024, 92, 3), (bad.MaxPhotoBytes, bad.HeicJpegQuality, bad.FrameJpegQ));
    }

    [Fact]
    public async Task PhotoUpload_TakesPhotosUpTo40Mb_AndNamesTheLimitAboveIt()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draft = await OpenDraftAsync(h, s);

        // 25 MB passes the size check (and is then refused for its content, not its size).
        var content = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => s.Service.AddPhotoAsync(draft, "big.jpg", new byte[25 * 1024 * 1024], CancellationToken.None));
        Assert.Contains("not a JPEG or PNG", content.Message);

        var size = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => s.Service.AddPhotoAsync(draft, "huge.jpg", new byte[(40 * 1024 * 1024) + 1], CancellationToken.None));
        Assert.Equal("huge.jpg is larger than 40 MB.", size.Message);
    }

    [Fact]
    public async Task HeicConversion_LargerThanTheLimit_IsRefusedAsTooLarge()
    {
        using var h = new WallTestHarness();
        var jpeg = ExifJpeg.Build(CaptureScenario.TinyJpeg());
        var options = new WallCapturePipelineOptions { MaxPhotoBytes = jpeg.Length - 1 };
        using var s = new CaptureScenario(h, options: options, photoConverter: new FixedConverter(jpeg));
        var draft = await OpenDraftAsync(h, s);
        byte[] heic = [0, 0, 0, 24, .. "ftypheic"u8.ToArray(), 0, 0, 0, 0, .. new byte[64]];

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => s.Service.AddPhotoAsync(draft, "IMG_1.HEIC", heic, CancellationToken.None));

        Assert.Contains("once converted from HEIC", ex.Message);
    }

    [Fact]
    public void FrameExtraction_Arguments_TakeTheConfiguredJpegQAndWindow()
    {
        var defaults = CaptureVideoFrameExtractor.FfmpegArguments("/v.mp4", 7.5, "/t/c_%05d.jpg", 363).ToList();
        Assert.Equal("3", defaults[defaults.IndexOf("-q:v") + 1]);
        var tuned = CaptureVideoFrameExtractor.FfmpegArguments("/v.mp4", 7.5, "/t/c_%05d.jpg", 363, jpegQ: 2).ToList();
        Assert.Equal("2", tuned[tuned.IndexOf("-q:v") + 1]);
        Assert.Equal(CaptureVideoFrameExtractor.MaxCandidates(60), CaptureVideoFrameExtractor.MaxCandidates(60, 3));
        Assert.Equal((2 * 60 * 5) + 5, CaptureVideoFrameExtractor.MaxCandidates(60, 5));
    }

    [Fact]
    public void HeifConvert_TakesTheConfiguredQuality()
    {
        Assert.Equal(["-q", "97", "/a.heic", "/a.jpg"], HeifCapturePhotoConverter.Arguments("/a.heic", "/a.jpg", 97));
    }

    [Fact]
    public void LodLadder_Has2MLevel_OnlyForScenesFrom2Point5M()
    {
        Assert.Equal([40_000, 120_000, 250_000, 800_000, 2_000_000], SplatLodLadder.Targets);
        Assert.Equal(2_500_000, 2_000_000 / SplatLodLadder.MaxFraction, 3);
    }

    private static IConfigurationSection Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("Blocwerk");

    private static IConfiguration Root(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static async Task<Guid> OpenDraftAsync(WallTestHarness h, CaptureScenario s)
    {
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        return (await s.Service.CreateDraftAsync(h.WallId)).CaptureId;
    }

    private sealed class FixedConverter(byte[] jpeg) : ICapturePhotoConverter
    {
        public Task<byte[]> ToJpegAsync(byte[] heic, CancellationToken ct) => Task.FromResult(jpeg);
    }
}
