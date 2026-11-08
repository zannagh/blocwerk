// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.HoldDetection;
using Blocwerk.HoldDetection.Markers;
using Blocwerk.HoldDetection.Matching;
using Blocwerk.Recognition.Worker;
using Blocwerk.Recognition.Worker.Jobs;
using Blocwerk.Recognition.Worker.Models;
using Blocwerk.Recognition.Worker.Protocol;
using Blocwerk.Recognition.Worker.Runtime;
using Serilog;

// The recognition worker (design R4): a runner for the recognition kinds, always on next to the app (the instance runner)
// or on someone's machine with a bwr_ key. Its only inbound endpoint is GET /health for the probes.
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
var builder = WebApplication.CreateSlimBuilder(args);
var options = WorkerOptions.Bind(builder.Configuration);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new RunnerApiClient(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, options));
builder.Services.AddSingleton<IMemoryProbe, CgroupMemoryProbe>();
builder.Services.AddSingleton<WorkerHealth>();
builder.Services.AddSingleton<WorkerExit>();
builder.Services.AddSingleton<ModelStore>();
builder.Services.AddSingleton<YoloBoxDetector>();
builder.Services.AddSingleton<IMarkerDetectionService, ArucoMarkerDetectionService>();
builder.Services.AddSingleton<IHoldPresenceProbe, OpenCvHoldPresenceProbe>();
builder.Services.AddSingleton<IHoldOverlapMatcher, OpenCvHoldOverlapMatcher>();
builder.Services.AddSingleton<IPhotoTextureMatcher, OpenCvPhotoTextureMatcher>();

// MobileSAM from the models mount, loaded at the first outline job (after the warm YOLO has created the ONNX runtime);
// without its files the outliner works by colour, as the app does.
var segmenter = new HoldSegmenterSettings
{
    EncoderPath = Path.Combine(options.ModelsDirectory, "hold-segmenter-encoder.onnx"),
    DecoderPath = Path.Combine(options.ModelsDirectory, "hold-segmenter-decoder.onnx"),
    Threads = 2,
};
var missingSegmenter = new[] { segmenter.EncoderPath, segmenter.DecoderPath }.Where(f => !File.Exists(f)).ToList();
if (missingSegmenter.Count == 0)
{
    Log.Information("Outlines: MobileSAM ({Encoder}, {Decoder}), loaded at the first outline job; by colour if it cannot be loaded", segmenter.EncoderPath, segmenter.DecoderPath);
}
else
{
    Log.Warning("Outlines: by colour only, because the segmenter model files are missing: {Missing}", missingSegmenter);
}

builder.Services.AddSingleton<IHoldOutlineService>(sp => HoldDetectionServices.Outliner(
    segmenter,
    logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger("Blocwerk.HoldDetection.Segmentation.HoldSegmenter")));
builder.Services.AddSingleton<IRecognitionJobHandler, DetectHoldsHandler>();
builder.Services.AddSingleton<IRecognitionJobHandler, MarkersHandler>();
builder.Services.AddSingleton<IRecognitionJobHandler, PresenceHandler>();
builder.Services.AddSingleton<IRecognitionJobHandler, MatchPanelsHandler>();
builder.Services.AddSingleton<IRecognitionJobHandler, OutlineHoldsHandler>();
builder.Services.AddSingleton<IRecognitionJobHandler, TextureMatchHandler>();
builder.Services.AddSingleton<JobExecutor>();
builder.Services.AddHostedService<RunnerLoop>();

var app = builder.Build();

// Liveness: a claim, hello or heartbeat was attempted recently. Readiness: models warm, hello answered, the server heard from recently.
app.MapGet("/health", (WorkerHealth health) => Results.Json(health.Describe(), statusCode: health.Live ? 200 : 503));
app.MapGet("/health/ready", (WorkerHealth health) => Results.Json(health.Describe(), statusCode: health.Ready ? 200 : 503));

app.Run();
