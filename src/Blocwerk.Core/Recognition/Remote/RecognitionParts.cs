// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Helpers;
using SkiaSharp;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>The envelope parts of a recognition job: the <c>request</c> JSON, the photos, and the memory they need on a runner.</summary>
public static class RecognitionParts
{
    /// <summary>What an image whose header cannot be read is taken for: a 48 MP phone photo.</summary>
    public const long UnreadablePixels = 48_000_000;

    /// <summary>The contracts' JSON: camelCase, enums by name, nulls left out.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The <c>request</c> part; <paramref name="request"/> is serialized with <see cref="Json"/>.</summary>
    public static ComputeJobPart Request(object request) => ComputeJobPart.Json("request", JsonSerializer.Serialize(request, Json));

    /// <summary>An image part; the file name is what the result keys it by.</summary>
    public static ComputeJobPart Image(string name, string fileName, byte[] image) => ComputeJobPart.File(name, fileName, image, "image/jpeg");

    /// <summary>
    /// What a job needs on a runner, from the images' headers (no decode), design §4: 300 MB plus 12 bytes per pixel
    /// (RGBA raster, BGR Mat, grey and Lab copies) of the images held at once (<paramref name="together"/>) or of the
    /// largest one (photos taken one after the other). An image whose header cannot be read counts as
    /// <see cref="UnreadablePixels"/>, so it never makes a job look small. Null without any image. The app's photos are
    /// JPEG by then (staged and capture HEICs are converted on upload), so a HEIC header is not read here.
    /// </summary>
    public static int? MemoryMb(IEnumerable<byte[]> images, bool together) => MemoryMb(images.Select(ImagePixelLimit.ReadSize), together);

    /// <summary>Like <see cref="MemoryMb(IEnumerable{byte[]}, bool)"/>, from sizes already read (null: unreadable).</summary>
    public static int? MemoryMb(IEnumerable<(int Width, int Height)?> sizes, bool together)
    {
        var pixels = sizes.Select(s => s is { } size ? (long)size.Width * size.Height : UnreadablePixels).ToList();
        if (pixels.Count == 0)
        {
            return null;
        }

        var held = together ? pixels.Sum() : pixels.Max();
        return (int)(300 + (held * 12 / (1024 * 1024)));
    }

    /// <summary>An image file's size from its header (no decode), or null.</summary>
    public static (int Width, int Height)? SizeOf(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
