// <copyright file="RedetectRun.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Redetect;
using Blocwerk.Core.Services;
using Blocwerk.HoldDetection;
using Blocwerk.HoldDetection.Markers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.HoldEval;

/// <summary>
/// Plans the app's own re-detect for every live panel of a wall in a database COPY and writes what it proposes. It
/// plans only: nothing is applied. The copy is first brought to this build's schema, so never point it at anything
/// but a throwaway copy.
/// </summary>
public static class RedetectRun
{
    /// <summary>Plans the wall's panels and writes one JSON per panel.</summary>
    /// <param name="modelPath">The detector model file; the segmenter's files lie next to it.</param>
    /// <param name="outDir">Where the JSON goes.</param>
    /// <param name="connection">The connection string of the copy.</param>
    /// <param name="wallId">The wall.</param>
    /// <returns>A task.</returns>
    public static async Task RunAsync(string modelPath, string outDir, string connection, Guid wallId)
    {
        var factory = new CopyHost(new DbContextOptionsBuilder<BlocwerkDbContext>().UseNpgsql(connection).Options);
        Guid owner;
        List<Guid> panels;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.Id == wallId);
            owner = wall.OwnerId;
            db.CurrentUserId = owner;
            panels = [.. await LiveWallHolds.LoadPanelIdsAsync(db, wallId, CancellationToken.None)];
        }

        var settings = new BlocwerkSettings();
        settings.HoldDetection.Segmenter.EncoderPath = Path.Join(Path.GetDirectoryName(modelPath)!, "hold-segmenter-encoder.onnx");
        settings.HoldDetection.Segmenter.DecoderPath = Path.Join(Path.GetDirectoryName(modelPath)!, "hold-segmenter-decoder.onnx");
        using var detector = new YoloHoldDetectionService(modelPath, settings.HoldDetection.Tiling);
        var enrichment = new HoldEnrichmentService(
            settings, NullLogger<HoldEnrichmentService>.Instance, HoldDetectionServices.Outliner(settings.HoldDetection.Segmenter, detector), new ArucoMarkerDetectionService());
        var service = new PanelRedetectService(
            factory, factory.ActingAs(owner), detector, new HoldSamenessOptions(), NullLogger<PanelRedetectService>.Instance, enrichment, CopyHost.MergesPossible(), markers: new ArucoMarkerDetectionService());
        foreach (var panel in panels)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var plan = await service.PlanAsync(wallId, panel);
            var target = Path.Join(outDir, $"{panel}.json");
            await File.WriteAllTextAsync(target, JsonSerializer.Serialize(Describe(plan), EvalJson.Options));
            Console.WriteLine($"{panel}: {plan.Detections.Count} detections, {plan.Proposals.Count} proposals, {plan.TurnedDown} turned down, {clock.Elapsed.TotalSeconds:F0} s -> {target}");
        }
    }

    private static object Describe(PanelRedetectPlan plan) => new
    {
        plan.PanelId,
        plan.PhotoAspect,
        plan.TurnedDown,
        plan.Untraced,
        plan.Summary,
        Holds = plan.Detections.Select((d, i) => new EvalHold
        {
            Index = i,
            X = d.Shape.X,
            Y = d.Shape.Y,
            Radius = d.Shape.Radius,
            DetectedRadius = d.Shape.Radius,
            BoxWidth = d.Shape.DetectedBox?.Width,
            BoxHeight = d.Shape.DetectedBox?.Height,
            Volume = d.Shape.Color == "white" && d.Color != "white",
            Confidence = d.Shape.Confidence,
            Method = d.Shape.ShapePoints is null ? "CircleFallback" : "Outline",
            OutlineConfidence = d.Shape.OutlineConfidence ?? 0,
            ShapePoints = d.Shape.ShapePoints?.Select(p => new[] { p.Dx, p.Dy }).ToList(),
            ShapeHoles = d.Shape.ShapeHoles?.Select(ring => ring.Select(p => new[] { p.Dx, p.Dy }).ToList()).ToList(),
            Color = d.Color,
            SizeMm = d.Shape.WidthMm is > 0 && d.Shape.HeightMm is > 0 ? Math.Max(d.Shape.WidthMm.Value, d.Shape.HeightMm.Value) : null,
            Pale = d.Shape.OutlinePale,
        }),
        Proposals = plan.Proposals.Select(p => new { p.Key, Kind = p.Kind.ToString(), p.HoldId, p.OtherHoldIds, p.Detections, p.Safe, p.Unsure, Doubt = p.Doubt.ToString(), Earlier = p.Earlier?.Kind.ToString(), p.Score }),
    };
}
