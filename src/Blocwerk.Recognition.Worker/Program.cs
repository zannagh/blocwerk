// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
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
builder.Services.AddSingleton<ModelStore>();
builder.Services.AddSingleton<YoloBoxDetector>();
builder.Services.AddSingleton<IMarkerDetectionService, ArucoMarkerDetectionService>();
builder.Services.AddSingleton<IHoldPresenceProbe, OpenCvHoldPresenceProbe>();
builder.Services.AddSingleton<IHoldOverlapMatcher, OpenCvHoldOverlapMatcher>();
builder.Services.AddSingleton<IRecognitionJobHandler, DetectHoldsHandler>();
builder.Services.AddSingleton<IRecognitionJobHandler, MarkersHandler>();
builder.Services.AddSingleton<IRecognitionJobHandler, PresenceHandler>();
builder.Services.AddSingleton<IRecognitionJobHandler, MatchPanelsHandler>();
builder.Services.AddSingleton<JobExecutor>();
builder.Services.AddHostedService<RunnerLoop>();

var app = builder.Build();

// Liveness: the process answers. Readiness: models warm, hello answered, the server heard from recently.
app.MapGet("/health", (WorkerHealth health) => Results.Json(health.Describe()));
app.MapGet("/health/ready", (WorkerHealth health) => Results.Json(health.Describe(), statusCode: health.Ready ? 200 : 503));

app.Run();
