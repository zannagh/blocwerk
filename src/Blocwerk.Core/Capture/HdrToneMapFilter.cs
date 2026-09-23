// <copyright file="HdrToneMapFilter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>
/// Picks the ffmpeg filter chain that tone-maps an HDR capture video (iPhone HLG / Dolby Vision 8.4,
/// or PQ) down to SDR BT.709, from what the installed ffmpeg offers. Without it the 10-bit HLG signal
/// is written as if it were BT.709: washed out, too bright and cool. Pure, so the choice is testable.
/// <list type="number">
/// <item><c>zscale</c> + <c>tonemap</c> (hable): the reference path; Ubuntu's ffmpeg (the Docker image) has it.</item>
/// <item>swscale's own colour management (<c>scale=…:intent=perceptual</c>, FFmpeg ≥ 8): nearly the
/// same result (mean luma within 1 %) where zscale is not built in, e.g. Homebrew's ffmpeg.</item>
/// <item><c>colorspace</c>: last resort. It cannot linearise HLG, so it only fixes the gamut (the cool
/// cast), not the brightness.</item>
/// </list>
/// <c>libplacebo</c> is deliberately never chosen: it is listed by Ubuntu's ffmpeg but fails at runtime
/// without a Vulkan device, which a server container does not have.
/// </summary>
public static class HdrToneMapFilter
{
    /// <summary>ffprobe's <c>color_transfer</c> for HLG (iPhone HDR video, Dolby Vision 8.4).</summary>
    public const string Hlg = "arib-std-b67";

    /// <summary>ffprobe's <c>color_transfer</c> for PQ (HDR10, Dolby Vision 8.1).</summary>
    public const string Pq = "smpte2084";

    /// <summary>
    /// Pseudo filter name for "the <c>scale</c> filter understands <c>out_transfer</c> and <c>intent</c>",
    /// which <c>ffmpeg -filters</c> cannot tell; <see cref="FfmpegFilterCatalog"/> adds it from <c>-h filter=scale</c>.
    /// </summary>
    public const string ScaleColorManagement = "scale+colormanagement";

    public const string ZscaleChain =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,"
        + "zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    public const string ScaleChain =
        "scale=out_transfer=bt709:out_primaries=bt709:out_color_matrix=bt709:out_range=tv:intent=perceptual,format=yuv420p";

    public const string ColorspaceChain = "colorspace=all=bt709:iall=bt2020:fast=0:format=yuv420p";

    /// <summary>True for an HDR transfer characteristic (HLG or PQ).</summary>
    public static bool IsHdr(string? colorTransfer) =>
        string.Equals(colorTransfer, Hlg, StringComparison.OrdinalIgnoreCase)
        || string.Equals(colorTransfer, Pq, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tone-mapping chain for a video with <paramref name="colorTransfer"/>, given the filters
    /// ffmpeg offers; null for SDR video or when no usable filter exists (frames then stay untouched).
    /// </summary>
    public static string? Select(string? colorTransfer, IReadOnlySet<string> availableFilters)
    {
        if (!IsHdr(colorTransfer))
        {
            return null;
        }

        if (availableFilters.Contains("zscale") && availableFilters.Contains("tonemap"))
        {
            return ZscaleChain;
        }

        if (availableFilters.Contains(ScaleColorManagement))
        {
            return ScaleChain;
        }

        return availableFilters.Contains("colorspace") ? ColorspaceChain : null;
    }
}
