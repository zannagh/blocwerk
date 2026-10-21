// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;

namespace Blocwerk.Core.Services;

/// <summary>The name rules of <see cref="FileSystemImageVariantCache"/>'s files: which key parts are safe in a path.</summary>
internal static class ImageVariantNames
{
    /// <summary>
    /// Lowercase hex only. Both key parts are hashes produced by the byte routes, so anything else
    /// is either a bug or an attempt to escape the cache root.
    /// </summary>
    public static bool IsHash(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A content hash: exactly 64 lowercase hex characters.</summary>
    public static bool IsSha(string value) => value.Length == ContentHash.Length * 2 && IsHash(value);

    /// <summary>The content type a rendition stored under <paramref name="extension"/> is served as.</summary>
    public static string TypeOf(string extension) => extension == ".webp" ? "image/webp" : "image/jpeg";
}
