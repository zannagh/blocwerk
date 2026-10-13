// <copyright file="EvalPipeline.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds.Colors;
using Blocwerk.Core.HoldStacks;
using Blocwerk.HoldDetection;
using Blocwerk.HoldDetection.Markers;
using Blocwerk.HoldDetection.Outlines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.HoldEval;

/// <summary>
/// What the app does to a fresh panel photo, without a database: detect, outline and clean up through the app's own
/// <see cref="HoldEnrichmentService"/>, drop detections on marker sheets, stack, read the colours.
/// </summary>
public sealed class EvalPipeline : IDisposable
{
    private readonly EvalSettings settings;
    private readonly YoloHoldDetectionService detector;
    private readonly OpenCvHoldOutlineService outliner;
    private readonly ArucoMarkerDetectionService markers = new();
    private readonly HoldEnrichmentService enrichment;

    /// <summary>Initializes a new instance of the <see cref="EvalPipeline"/> class.</summary>
    /// <param name="modelPath">The detector model file.</param>
    /// <param name="settings">The run's settings.</param>
    public EvalPipeline(string modelPath, EvalSettings settings)
    {
        this.settings = settings;
        var app = new BlocwerkSettings();
        app.HoldDetection.BoxSeeds = settings.BoxSeed;
        app.HoldDetection.Tiling.TileSize = settings.TileSize > 0 ? settings.TileSize : app.HoldDetection.Tiling.TileSize;
        app.HoldDetection.Tiling.Confidence = settings.Confidence > 0 ? settings.Confidence : app.HoldDetection.Tiling.Confidence;
        app.HoldDetection.Segmenter.Enabled = settings.Segmenter;
        app.HoldDetection.Segmenter.EncoderPath = Path.Combine(Path.GetDirectoryName(modelPath)!, "hold-segmenter-encoder.onnx");
        app.HoldDetection.Segmenter.DecoderPath = Path.Combine(Path.GetDirectoryName(modelPath)!, "hold-segmenter-decoder.onnx");
        detector = new YoloHoldDetectionService(modelPath, app.HoldDetection.Tiling);
        outliner = HoldDetectionServices.Outliner(app.HoldDetection.Segmenter, detector);
        enrichment = new HoldEnrichmentService(app, NullLogger<HoldEnrichmentService>.Instance, outliner);
    }

    /// <summary>Runs one photo.</summary>
    /// <param name="photo">The encoded photo.</param>
    /// <returns>Every detection with what each stage made of it.</returns>
    public async Task<EvalRun> RunAsync(byte[] photo)
    {
        var clock = Stopwatch.StartNew();
        var detected = await detector.DetectHoldsAsync(photo);
        var detectMs = clock.Elapsed.TotalMilliseconds;
        var holds = detected.Select(ToHold).ToList();

        clock.Restart();
        var traced = Trace(photo, holds, out var size);
        var tracedMs = clock.Elapsed.TotalMilliseconds;

        // The wall has no markers switched on, so the enrichment never asks the database for anything.
        await using var db = new BlocwerkDbContext(new DbContextOptionsBuilder<BlocwerkDbContext>().UseNpgsql("Host=unused").Options);
        await enrichment.EnrichAsync(db, new HoldEnrichmentRequest(photo, new Wall { Name = "evaluation" }, holds));
        var outlineMs = clock.Elapsed.TotalMilliseconds - tracedMs;

        var onMarkers = settings.Markers ? await OnMarkersAsync(photo, holds) : [];
        var kept = holds.Where(h => !onMarkers.Contains(h)).ToList();
        var stacks = HoldStacking.Plan(kept, null, size).ToDictionary(p => p.Upper, p => holds.IndexOf(p.Base));
        var colors = Colors(photo, holds);
        var result = holds.Select((h, i) => new EvalHold
        {
            Index = i,
            X = h.X,
            Y = h.Y,
            Radius = h.Radius,
            DetectedRadius = detected[i].Radius,
            BoxWidth = detected[i].BoxWidth,
            BoxHeight = detected[i].BoxHeight,
            Volume = detected[i].Color == "white",
            Confidence = h.Confidence,
            Method = traced is null ? (h.ShapePoints is null ? "CircleFallback" : "Outline") : traced[i].Method.ToString(),
            OutlineConfidence = h.OutlineConfidence ?? 0,
            Traced = traced?[i].ShapePoints is null ? null : [.. traced[i].Polygon.Select(p => new[] { p.X, p.Y })],
            ShapePoints = h.ShapePoints?.Select(p => new[] { p.Dx, p.Dy }).ToList(),
            ShapeHoles = h.ShapeHoles?.Select(ring => ring.Select(p => new[] { p.Dx, p.Dy }).ToList()).ToList(),
            Color = colors[i],
            StackedOn = stacks.TryGetValue(h, out var on) ? on : null,
            OnMarker = onMarkers.Contains(h),
        }).ToList();
        return new EvalRun(size.Width, size.Height, detectMs, outlineMs, settings, result);
    }

    /// <inheritdoc/>
    public void Dispose() => detector.Dispose();

    private static Hold ToHold(DetectedHold d) => new()
    {
        X = d.X,
        Y = d.Y,
        Radius = d.Radius,
        Color = d.Color,
        Confidence = d.Confidence,
        IsAutoDetected = true,
        NeedsReview = true,
        DetectedBox = HoldDetectedBox.Of(d),
    };

    private static List<string?> Colors(byte[] photo, List<Hold> holds)
    {
        using var pixels = HoldPhotoPixels.TryOpen(photo);
        return [.. holds.Select(h => pixels is null ? null : HoldColorSampler.Estimate(h, pixels.Width, pixels.Height, pixels.At).Color)];
    }

    /// <summary>The outliner's own answer per hold, before the panel's overlap clean-up.</summary>
    private List<HoldOutlineResult>? Trace(byte[] photo, List<Hold> holds, out (int Width, int Height) size)
    {
        using var session = outliner.OpenSession(photo);
        size = (session.ImageWidth, session.ImageHeight);
        return !settings.Traced ? null : [.. session.OutlineEach([.. holds.Select(h => HoldSeeds.Of(h, settings.BoxSeed))])];
    }

    private async Task<HashSet<Hold>> OnMarkersAsync(byte[] photo, List<Hold> holds)
    {
        var detection = await markers.DetectAsync(photo, null, CancellationToken.None);
        return [.. MarkerHoldFilter.HoldsOnMarkers(holds, detection.Markers)];
    }
}
