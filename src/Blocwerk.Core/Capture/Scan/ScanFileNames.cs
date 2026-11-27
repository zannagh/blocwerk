// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.RegularExpressions;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// The names a package file may have: one of four folders and a plain file name, so a name can never leave the scan's
/// folder on disk (<c>video/seg0-000.mov</c>, <c>depth/d000123.png</c>, <c>confidence/d000123.png</c>,
/// <c>mesh/arkit.ply</c>). The pattern ends with <c>\z</c>, not <c>$</c>: <c>$</c> also matches before a final newline,
/// which would let <c>mesh/arkit.ply\n</c> through as a name.
/// </summary>
public static partial class ScanFileNames
{
    /// <summary>The kind of a package file name, or <see cref="ScanFileKind.Invalid"/>.</summary>
    public static ScanFileKind KindOf(string? name)
    {
        if (name is null || Pattern().Match(name) is not { Success: true } match)
        {
            return ScanFileKind.Invalid;
        }

        return (match.Groups["dir"].Value, match.Groups["ext"].Value) switch
        {
            ("video", "mov" or "mp4") => ScanFileKind.Video,
            ("depth", "png") => ScanFileKind.Depth,
            ("confidence", "png") => ScanFileKind.Confidence,
            ("mesh", "ply") => ScanFileKind.Mesh,
            _ => ScanFileKind.Invalid,
        };
    }

    [GeneratedRegex("^(?<dir>video|depth|confidence|mesh)/[A-Za-z0-9_-]{1,64}\\.(?<ext>mov|mp4|png|ply)\\z")]
    private static partial Regex Pattern();
}
