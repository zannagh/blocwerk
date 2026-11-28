// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Reads a HEIC's image sizes from its header without decoding: every <c>ispe</c> (image spatial extents) property box
/// of the file, so a caller can refuse a picture over the pixel limit before handing it to a converter. A grid image's
/// own <c>ispe</c> names the whole picture; its tiles name less, so the largest one counts.
/// </summary>
public static class HeicHeader
{
    private static ReadOnlySpan<byte> Ispe => "ispe"u8;

    /// <summary>The largest image the header declares, or null when it declares none (not a readable HEIC).</summary>
    /// <param name="heic">The file's bytes.</param>
    /// <returns>Width and height in pixels, or null.</returns>
    public static (int Width, int Height)? LargestImage(ReadOnlySpan<byte> heic)
    {
        (int Width, int Height)? largest = null;
        var offset = 0;
        while (offset < heic.Length)
        {
            var at = heic[offset..].IndexOf(Ispe);
            if (at < 0)
            {
                break;
            }

            var type = offset + at;
            offset = type + Ispe.Length;

            // A full box: size (4) before the type, version and flags (4), then width (4) and height (4).
            if (type < 4 || type + 16 > heic.Length || BinaryPrimitives.ReadUInt32BigEndian(heic[(type - 4)..]) < 20)
            {
                continue;
            }

            var width = BinaryPrimitives.ReadUInt32BigEndian(heic[(type + 8)..]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(heic[(type + 12)..]);
            if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
            {
                continue;
            }

            if (largest is not { } best || (long)width * height > (long)best.Width * best.Height)
            {
                largest = ((int)width, (int)height);
            }
        }

        return largest;
    }
}
