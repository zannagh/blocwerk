// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Authentication.Services;

/// <summary>
/// Whether an upload's first bytes are the format its declared content type names: JPEG (<c>FF D8 FF</c>), PNG (the
/// eight-byte PNG signature) or WebP (<c>RIFF</c>, four size bytes, <c>WEBP</c>). The decoder would read any of them
/// whatever the header said; checking keeps a mislabelled or disguised file from reaching it at all.
/// </summary>
public static class AvatarImageSignature
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] Webp = "WEBP"u8.ToArray();

    /// <summary>True when <paramref name="image"/> starts as <paramref name="contentType"/> says it should.</summary>
    public static bool Matches(ReadOnlySpan<byte> image, string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => image.StartsWith(Jpeg),
        "image/png" => image.StartsWith(Png),
        "image/webp" => image.Length >= 12 && image.StartsWith(Riff) && image[8..12].SequenceEqual(Webp),
        _ => false,
    };
}
