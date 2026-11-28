// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using Blocwerk.Authentication.Services;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The avatar upload's two guards ahead of the decoder: the file's first bytes must be the declared format, and the
/// header may declare at most <see cref="AvatarImageEncoder.MaxPixels"/> pixels, so a tiny file claiming a huge image is
/// refused before its pixels are allocated.
/// </summary>
public sealed class AvatarUploadGuardTests
{
    [Fact]
    public void TheSignature_MustMatchTheDeclaredType()
    {
        var png = Png(8, 8);

        Assert.True(AvatarImageSignature.Matches(png, "image/png"));
        Assert.True(AvatarImageSignature.Matches(png, "IMAGE/PNG"));
        Assert.False(AvatarImageSignature.Matches(png, "image/jpeg"));
        Assert.False(AvatarImageSignature.Matches(png, "image/webp"));
        Assert.True(AvatarImageSignature.Matches([0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg"));
        Assert.True(AvatarImageSignature.Matches("RIFF\0\0\0\0WEBPVP8 "u8.ToArray(), "image/webp"));
        Assert.False(AvatarImageSignature.Matches("RIFF\0\0\0\0WAVEfmt "u8.ToArray(), "image/webp"));
        Assert.False(AvatarImageSignature.Matches([], "image/png"));
    }

    [Fact]
    public void AHeaderDeclaringTooManyPixels_IsRefusedBeforeDecoding()
    {
        var bomb = WithDeclaredSize(Png(8, 8), 10_000, 10_000);
        Assert.Equal((10_000, 10_000), AvatarImageEncoder.Measure(bomb));

        var refused = Assert.Throws<InvalidOperationException>(() => AvatarImageEncoder.Scale(bomb));

        Assert.Contains("megapixels", refused.Message);
    }

    [Fact]
    public void AnOrdinaryPhoto_StillScales()
    {
        var (image, contentType) = AvatarImageEncoder.Scale(Png(800, 600));

        Assert.Equal("image/webp", contentType);
        Assert.Equal((512, 384), AvatarImageEncoder.Measure(image));
    }

    internal static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    // Rewrites the IHDR chunk's width and height (bytes 16-23) and its CRC (bytes 29-32), so the header is valid and
    // declares a size the pixel data does not have.
    private static byte[] WithDeclaredSize(byte[] png, int width, int height)
    {
        var bytes = (byte[])png.Clone();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29), Crc32(bytes.AsSpan(12, 17)));
        return bytes;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
