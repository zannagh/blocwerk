// <copyright file="WebpChunk.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>One top-level RIFF chunk of a WebP file, as found by the bounds-checked walk.</summary>
/// <param name="FourCc">The chunk type ("VP8X", "VP8 ", "ICCP", "EXIF", "XMP "…).</param>
/// <param name="Start">Offset of the chunk header (the FourCC) in the file.</param>
/// <param name="Size">The payload length, excluding the 8-byte header and the pad byte.</param>
internal readonly record struct WebpChunk(string FourCc, int Start, int Size);
