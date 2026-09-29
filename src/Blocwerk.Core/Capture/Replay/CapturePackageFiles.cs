// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The files of a capture package, derived from its rows only (export and import agree on them by construction), plus
/// the package's JSON form and the streamed SHA-256.
/// </summary>
public static partial class CapturePackageFiles
{
    /// <summary>The package JSON: web casing, nulls (the rows' unloaded navigations) left out.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Every stored name the rows point at, with its role. The job's bundle is gone after training and not part of it.</summary>
    public static IReadOnlyList<(string Name, string Role)> Referenced(CapturePackageRows rows)
    {
        var names = new List<(string Name, string Role)>();
        names.AddRange(rows.Photos.Select(p => (p.StoredPath, "photo")));
        if (rows.Capture.VideoStoredPath is { } video)
        {
            names.Add((video, "video"));
        }

        names.AddRange(CaptureVideoFiles.Frames(rows.Capture.VideoFramesJson).Select(f => (f, "video-frame")));
        if (rows.Capture.SparsePointsStoredPath is { } sparse)
        {
            names.Add((sparse, "sparse-points"));
        }

        foreach (var texture in rows.Textures)
        {
            names.Add((texture.StoredPath, "texture"));
            AddIf(names, texture.MaskStoredPath, "texture-mask");
            AddIf(names, texture.SourceMapStoredPath, "texture-source-map");
        }

        names.Add((rows.GpuJob.PreparedPath, "prepared"));
        AddIf(names, rows.GpuJob.ResultPath, "trained-result");
        return names.DistinctBy(n => n.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>True for a name the capture store hands out: 32 hex digits and a short extension, nothing else.</summary>
    public static bool IsStoredName(string? name) => name is not null && StoredName().IsMatch(name);

    /// <summary>SHA-256 of a file, lowercase hex.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/> from <paramref name="source"/> to <paramref name="target"/>, hashing on the
    /// way; returns the byte count and SHA-256 (lowercase hex). Stops with a <see cref="CaptureFileTooLargeException"/> past the cap.
    /// </summary>
    public static async Task<(long Bytes, string Sha256)> CopyHashedAsync(Stream source, Stream target, long maxBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new CaptureFileTooLargeException(maxBytes);
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AddIf(List<(string Name, string Role)> names, string? name, string role)
    {
        if (name is not null)
        {
            names.Add((name, role));
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.[a-z0-9]{1,5}$")]
    private static partial Regex StoredName();
}
