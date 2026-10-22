// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>The Brotli copy a document is sent as to a browser that accepts <c>br</c>.</summary>
public static class DocumentBrotli
{
    public static byte[] Compress(byte[] json)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(json);
        }

        return output.ToArray();
    }
}
