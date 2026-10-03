// <copyright file="CaptureVideoJoiner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Refresh;

/// <summary>What joining a visit's videos into the capture's one video came to.</summary>
/// <param name="Path">The video to hand to the capture (a new file when joined, else one of the inputs).</param>
/// <param name="Joined">True when <paramref name="Path"/> is a new, joined file the caller deletes afterwards.</param>
/// <param name="Note">What happened, in plain words.</param>
public sealed record VideoJoinResult(string Path, bool Joined, string Note);

/// <summary>Turns several videos into the one the capture takes.</summary>
public interface ICaptureVideoJoiner
{
    Task<VideoJoinResult> JoinAsync(IReadOnlyList<string> paths, CancellationToken ct);
}

/// <summary>
/// Joins videos without re-encoding (ffmpeg's concat demuxer, stream copy) when their video streams match
/// (codec, size, pixel format, frame rate, rotation); otherwise keeps the longest one and says so.
/// </summary>
public sealed class CaptureVideoJoiner(BlocwerkSettings settings) : ICaptureVideoJoiner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromMinutes(30);

    public async Task<VideoJoinResult> JoinAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 1)
        {
            return new VideoJoinResult(paths[0], false, "One video");
        }

        var probes = new List<VideoStreamSignature>();
        foreach (var path in paths)
        {
            probes.Add(await ProbeAsync(path, ct));
        }

        if (!CanJoin(probes))
        {
            var longest = probes.MaxBy(p => p.DurationSeconds)!;
            return new VideoJoinResult(
                longest.Path, false, $"The {paths.Count} videos differ in format, so only the longest one is used ({longest.DurationSeconds:0} s).");
        }

        var output = await ConcatAsync(paths, ct);
        return new VideoJoinResult(output, true, $"{paths.Count} videos joined into one ({probes.Sum(p => p.DurationSeconds):0} s).");
    }

    /// <summary>True when every stream has the same codec parameters (so a stream copy is valid).</summary>
    public static bool CanJoin(IReadOnlyList<VideoStreamSignature> probes) =>
        probes.Count > 0 && probes.All(p => p.Key.Length > 0 && p.Key == probes[0].Key);

    /// <summary>Reads ffprobe's JSON into a signature; <paramref name="path"/> is kept for the result.</summary>
    public static VideoStreamSignature Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        {
            return new VideoStreamSignature(path, string.Empty, 0);
        }

        var s = streams[0];
        var rotation = s.TryGetProperty("side_data_list", out var side) && side.ValueKind == JsonValueKind.Array
            ? side.EnumerateArray().Select(d => d.TryGetProperty("rotation", out var r) ? r.ToString() : null).FirstOrDefault(r => r is not null)
            : null;
        var key = string.Join('|', Text(s, "codec_name"), Text(s, "profile"), Text(s, "width"), Text(s, "height"), Text(s, "pix_fmt"), Text(s, "r_frame_rate"), rotation ?? "0");
        var duration = root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        return new VideoStreamSignature(path, key, duration);
    }

    /// <summary>One line of ffmpeg's concat list: the path in single quotes, a quote inside written as <c>'\''</c>.</summary>
    public static string ConcatLine(string path) => $"file '{path.Replace("'", @"'\''")}'";

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : string.Empty;

    private async Task<VideoStreamSignature> ProbeAsync(string path, CancellationToken ct)
    {
        string[] args =
        [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "format=duration:stream=codec_name,profile,width,height,pix_fmt,r_frame_rate:stream_side_data=rotation",
            "-of", "json", path,
        ];
        var json = await CaptureToolProcess.RunAsync(settings.BetaVideo.FfprobePath, args, ProbeTimeout, null, ct);
        return Parse(path, json);
    }

    private async Task<string> ConcatAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(paths[0])!;
        var list = Path.Combine(dir, $"{Guid.NewGuid():N}.txt");
        var output = Path.Combine(dir, $"{Guid.NewGuid():N}{Path.GetExtension(paths[0])}");
        await File.WriteAllLinesAsync(list, paths.Select(ConcatLine), ct);
        try
        {
            string[] args = ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-c", "copy", output];
            await CaptureToolProcess.RunAsync(settings.BetaVideo.FfmpegPath, args, JoinTimeout, null, ct);
            return output;
        }
        catch
        {
            File.Delete(output);
            throw;
        }
        finally
        {
            File.Delete(list);
        }
    }
}

/// <summary>A video's stream parameters that must match for a lossless join, and its length.</summary>
public sealed record VideoStreamSignature(string Path, string Key, double DurationSeconds);
