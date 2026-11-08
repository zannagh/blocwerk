// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition.Remote;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The recognition worker's half of an <c>outline-holds</c> job, played in the test where the natives are not loaded: it reads
/// the photo and seeds from the job's envelope, outlines them with an in-process batch and answers in the contract's form
/// (as <c>OutlineHoldsHandler.ToResult</c> writes it).
/// </summary>
internal static class FakeOutlineWorker
{
    /// <summary>Claims the next job, outlines it with <paramref name="outliner"/>, completes it; returns what the worker computed.</summary>
    public static async Task<(RecognitionJob Job, HoldOutlineBatchResult Outlined)> ServeAsync(
        FakeRecognitionClaimant pod, RecognitionFixture f, IHoldOutlineBatch outliner)
    {
        var job = await pod.ClaimAsync();
        var (request, photo) = Read(f, job);
        var seeds = request.GetProperty("seeds").EnumerateArray()
            .Select(s => new HoldSeed(
                s.GetProperty("x").GetDouble(), s.GetProperty("y").GetDouble(), s.GetProperty("radius").GetDouble(), Optional(s, "boxWidth"), Optional(s, "boxHeight")))
            .ToList();
        var options = new HoldOutlineBatchOptions { Joint = request.GetProperty("options").GetProperty("joint").GetBoolean() };
        var outlined = await outliner.OutlineAsync(photo, seeds, options);
        await pod.CompleteAsync(job, Result(outlined.Outlines));
        return (job, outlined);
    }

    public static string Result(IReadOnlyList<HoldOutlineResult> outlines) => JsonSerializer.Serialize(
        new
        {
            version = 1,
            outlines = outlines.Select((o, i) => new
            {
                id = i,
                polygon = o.Polygon.Select(p => new[] { p.X, p.Y }),
                anchorX = o.AnchorX,
                anchorY = o.AnchorY,
                shapePoints = o.ShapePoints,
                shapeHoles = o.ShapeHoles,
                areaPx = o.AreaPx,
                bounds = o.Bounds,
                confidence = o.Confidence,
                method = o.Method,
                fingerprint = o.Fingerprint,
                corroborated = o.Corroborated,
                distinct = o.Distinct,
                pale = o.Pale,
            }),
        },
        RecognitionParts.Json);

    /// <summary>The <c>request</c> part and the <c>photo</c> file of a job's input envelope.</summary>
    public static (JsonElement Request, byte[] Photo) Read(RecognitionFixture f, RecognitionJob job)
    {
        using var zip = ZipFile.OpenRead(f.Runners.Files.ResolvePhysicalPath(job.InputPath!)!);
        using var manifest = JsonDocument.Parse(zip.GetEntry("parts.json")!.Open());
        var parts = manifest.RootElement.GetProperty("parts").EnumerateArray().ToList();
        var request = parts.Single(p => p.GetProperty("name").GetString() == "request").GetProperty("json").Clone();
        var file = parts.Single(p => p.GetProperty("name").GetString() == "photo").GetProperty("file").GetString()!;
        using var entry = zip.GetEntry(file)!.Open();
        using var bytes = new MemoryStream();
        entry.CopyTo(bytes);
        return (request, bytes.ToArray());
    }

    private static double? Optional(JsonElement seed, string name) => seed.TryGetProperty(name, out var value) ? value.GetDouble() : null;
}
