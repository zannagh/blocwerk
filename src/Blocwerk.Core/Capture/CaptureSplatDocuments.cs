// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The documents exchanged with the splat worker (<c>docker/splat-worker/README.md</c>): the
/// <c>options</c> part of a <c>splat</c> job, its progress labels, and the <c>frame.json</c> it
/// returns next to <c>wall.spz</c>.
/// </summary>
public static class CaptureSplatDocuments
{
    public const string SpzFile = "wall.spz";

    public const string FrameFile = "frame.json";

    /// <summary>
    /// Name prefix of an AUXILIARY image in a splat request: a walk-along video frame. The worker
    /// trains on it but never aligns with it (<c>docker/splat-worker/README.md</c>). Photo names
    /// (<see cref="CaptureComputeDocuments.PhotoName"/>, <c>p01</c>…) can never start with it.
    /// </summary>
    public const string FramePrefix = "vf_";

    /// <summary>The request name (stem) of the <paramref name="number"/>-th video frame, 1-based: <c>vf_0001</c>.</summary>
    public static string FrameName(int number) => $"{FramePrefix}{number:D4}";

    /// <summary>
    /// The <c>options</c> JSON: the capture's quality profile (none recorded = the worker's default)
    /// and only what the server configured beyond it. A configured step count overrides the profile's.
    /// </summary>
    public static string BuildOptions(int? maxSteps, SplatQuality? quality = null)
    {
        var options = new JsonObject { ["spz"] = true };
        if (quality is { } q)
        {
            options["quality"] = QualityName(q);
        }

        if (maxSteps is > 0)
        {
            options["maxSteps"] = maxSteps.Value;
        }

        return options.ToJsonString();
    }

    /// <summary>The worker's name of a quality profile (<c>options.quality</c>).</summary>
    public static string QualityName(SplatQuality quality) => quality switch
    {
        SplatQuality.Draft => "draft",
        SplatQuality.Max => "max",
        _ => "high",
    };

    /// <summary>
    /// The profile a "retrain at higher quality" offers after <paramref name="current"/>: none recorded
    /// (a capture from before the profiles) or draft → high, high → max, max → nothing higher.
    /// </summary>
    public static SplatQuality? NextQuality(SplatQuality? current) => current switch
    {
        null or SplatQuality.Draft => SplatQuality.High,
        SplatQuality.High => SplatQuality.Max,
        _ => null,
    };

    /// <summary>"Photo-real view: training (step 1200/15000)" from the worker's <c>stage</c>/<c>stageDetail</c>.</summary>
    public static string Describe(ComputeJobStatus status)
    {
        var stage = status.Stage switch
        {
            null or "" or "starting" => status.Status == ComputeJobStates.Queued ? "waiting for the worker" : "starting",
            "ingest" => "preparing the photos",
            "sfm-pairs" => "choosing image pairs",
            "sfm-features" => "finding features",
            "sfm-matching" => "matching photos",
            "sfm-mapping" => "placing the cameras",
            "undistort" => "undistorting",
            "train" => "training",
            "align" => "aligning with the 3D model",
            "crop" or "export" => "exporting",
            _ => status.Stage,
        };
        return string.IsNullOrWhiteSpace(status.StageDetail)
            ? $"Photo-real view: {stage}"
            : $"Photo-real view: {stage} ({status.StageDetail})";
    }

    /// <summary>
    /// Checks a downloaded <c>frame.json</c>: it must be aligned to the wall geometry and carry a
    /// finite 4×4 <c>toWorldMm</c>. Throws <see cref="InvalidDataException"/> with a reason otherwise.
    /// </summary>
    public static void Validate(string frameJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(frameJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("aligned", out var aligned) || aligned.ValueKind != JsonValueKind.True)
            {
                throw new InvalidDataException("the photo-real scene could not be aligned with the 3D model.");
            }

            if (WorldMatrixOrNull(root) is null)
            {
                throw new InvalidDataException("the photo-real scene came without a usable transform.");
            }
        }
        catch (JsonException)
        {
            throw new InvalidDataException("the photo-real scene's frame.json is not valid JSON.");
        }
    }

    /// <summary>
    /// The splat → wall-geometry-world (mm) transform as a column-major 4×4 (three.js
    /// <c>Matrix4.fromArray</c>), from the frame's row-major <c>toWorldMm</c>. Null when missing.
    /// </summary>
    public static double[]? WorldMatrix(string frameJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(frameJson);
            return WorldMatrixOrNull(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>For log lines: the frame's median alignment residual, if reported.</summary>
    public static string ResidualText(string frameJson)
    {
        var node = JsonNode.Parse(frameJson)?["alignment"]?["residualMmMedian"] as JsonValue;
        return node is not null && node.TryGetValue<double>(out var mm)
            ? mm.ToString("0.#", CultureInfo.InvariantCulture) + " mm"
            : "n/a";
    }

    private static double[]? WorldMatrixOrNull(JsonElement root)
    {
        if (!root.TryGetProperty("toWorldMm", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 4)
        {
            return null;
        }

        var m = new double[16];
        var r = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 4)
            {
                return null;
            }

            var c = 0;
            foreach (var cell in row.EnumerateArray())
            {
                if (cell.ValueKind != JsonValueKind.Number || !double.IsFinite(cell.GetDouble()))
                {
                    return null;
                }

                m[(c * 4) + r] = cell.GetDouble();
                c++;
            }

            r++;
        }

        return m;
    }
}
