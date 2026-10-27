// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text.Json;
using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The result of a panel registration job (<c>gpurunner/panels.py</c>): <c>{"version": 1, "panels": {panel name: {"width",
/// "height", "params": [f, cx, cy, k1, k2], "R": [9], "t": [3], "observations", "rmsPx", "medianPx"}}}</c>.
/// </summary>
public static class RunnerPanelPoses
{
    /// <summary>The <see cref="Entities.GpuJob.ResultFormat"/> of a panel registration result.</summary>
    public const string Format = "json";

    /// <summary>The largest result accepted.</summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    private const int MaxSidePx = 100_000;

    /// <summary>The poses per panel id of a stored result; throws <see cref="InvalidDataException"/> when it is not one.</summary>
    public static IReadOnlyDictionary<Guid, PanelPose> Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is 0 or > MaxBytes)
        {
            throw new InvalidDataException("The panel registration result is missing, empty or too large.");
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            return Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The panel registration result is not JSON: {ex.Message}");
        }
    }

    /// <summary>The poses of a parsed result document.</summary>
    public static IReadOnlyDictionary<Guid, PanelPose> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1
            || !root.TryGetProperty("panels", out var panels) || panels.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The panel registration result is not version 1.");
        }

        var result = new Dictionary<Guid, PanelPose>();
        foreach (var panel in panels.EnumerateObject())
        {
            if (panel.Name.Length < 34 || panel.Name[32] != 'r' || !Guid.TryParseExact(panel.Name[..32], "N", out var id)
                || !int.TryParse(panel.Name[33..], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            {
                throw new InvalidDataException("The panel registration result names an unknown panel.");
            }

            result[id] = Pose(panel.Name, revision, panel.Value);
        }

        return result;
    }

    private static PanelPose Pose(string name, int revision, JsonElement e)
    {
        var width = Int(e, "width");
        var height = Int(e, "height");
        var p = Numbers(e, "params", 5);
        var camera = new SolvedCamera(
            name, width, height, [p[0], 0, p[1], 0, p[0], p[2], 0, 0, 1], [p[3], p[4], 0, 0], Numbers(e, "R", 9), Numbers(e, "t", 3));
        return new PanelPose(camera, revision, Int(e, "observations"), Numbers(e, "rmsPx", 1)[0], Numbers(e, "medianPx", 1)[0]);
    }

    private static int Int(JsonElement e, string field) =>
        e.TryGetProperty(field, out var v) && v.TryGetInt32(out var i) && i is >= 0 and <= MaxSidePx
            ? i
            : throw new InvalidDataException($"The panel registration result has a bad {field}.");

    private static double[] Numbers(JsonElement e, string field, int count)
    {
        if (!e.TryGetProperty(field, out var v))
        {
            throw new InvalidDataException($"The panel registration result lacks {field}.");
        }

        var values = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [v];
        if (values.Count != count || values.Any(x => x.ValueKind != JsonValueKind.Number || !double.IsFinite(x.GetDouble())))
        {
            throw new InvalidDataException($"The panel registration result has a bad {field}.");
        }

        return values.Select(x => x.GetDouble()).ToArray();
    }
}
