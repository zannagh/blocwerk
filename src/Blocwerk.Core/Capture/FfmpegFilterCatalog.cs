// <copyright file="FfmpegFilterCatalog.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Which video filters the installed ffmpeg offers (<c>ffmpeg -hide_banner -filters</c>), plus
/// <see cref="HdrToneMapFilter.ScaleColorManagement"/> when <c>-h filter=scale</c> lists swscale's
/// colour management. Asked once per ffmpeg path per process; a failed query is not cached, so the
/// next extraction tries again.
/// </summary>
public static class FfmpegFilterCatalog
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> Cache = new(StringComparer.Ordinal);

    /// <summary>The filter names of the ffmpeg at <paramref name="ffmpegPath"/>; empty when it cannot be asked.</summary>
    public static async Task<IReadOnlySet<string>> GetAsync(string ffmpegPath, CancellationToken ct)
    {
        if (Cache.TryGetValue(ffmpegPath, out var cached))
        {
            return cached;
        }

        try
        {
            var list = await CaptureToolProcess.RunAsync(ffmpegPath, ["-hide_banner", "-filters"], QueryTimeout, null, ct);
            var scaleHelp = await CaptureToolProcess.RunAsync(ffmpegPath, ["-hide_banner", "-h", "filter=scale"], QueryTimeout, null, ct);
            var filters = Parse(list, scaleHelp);
            Cache[ffmpegPath] = filters;
            return filters;
        }
        catch (InvalidDataException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// <c>-filters</c> output → filter names. Rows look like <c> TS colorspace  V->V  Convert…</c>
    /// (flags, name, pads with "->", description); the legend above them has no "->" pad column.
    /// </summary>
    public static IReadOnlySet<string> Parse(string filtersOutput, string? scaleHelp)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in filtersOutput.Split('\n'))
        {
            var parts = line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[2].Contains("->", StringComparison.Ordinal))
            {
                names.Add(parts[1]);
            }
        }

        if (scaleHelp is not null && scaleHelp.Contains("out_transfer", StringComparison.Ordinal)
            && scaleHelp.Contains("perceptual", StringComparison.Ordinal))
        {
            names.Add(HdrToneMapFilter.ScaleColorManagement);
        }

        return names;
    }
}
