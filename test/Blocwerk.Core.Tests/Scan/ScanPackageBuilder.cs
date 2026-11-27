// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Capture.Scan;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>A small valid <c>blocwerk-scan/1</c> package: one segment, a few posed frames, depth samples, a mesh.</summary>
internal static class ScanPackageBuilder
{
    public const double Start = 100;

    /// <summary>Builds the manifest and the bytes of every file it lists.</summary>
    public static (ScanManifest Manifest, Dictionary<string, byte[]> Files) Build(Guid wallId, Guid? scanId = null, int frames = 10, int depth = 3)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["video/seg0-000.mov"] = Bytes(4096, 1),
            ["mesh/arkit.ply"] = Bytes(512, 2),
        };
        var depthFrames = new List<ScanDepthFrame>();
        for (var i = 0; i < depth; i++)
        {
            var name = $"d{i:D6}.png";
            files[$"depth/{name}"] = Bytes(300, 10 + i);
            files[$"confidence/{name}"] = Bytes(100, 50 + i);
            depthFrames.Add(new ScanDepthFrame(Time(i * 3), 0, $"depth/{name}", $"confidence/{name}", 256, 192));
        }

        var manifest = new ScanManifest
        {
            SchemaVersion = ScanPackageLimits.SchemaV1,
            ScanId = scanId ?? Guid.NewGuid(),
            WallId = wallId,
            StartedAt = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero),
            App = new ScanApp("1.0", "1"),
            Device = new ScanDevice("iPhone17,1", "26.1"),
            Video = new ScanVideoFormat("hevc", 3840, 2160, 30),
            ExposureLock = new ScanExposureLock(1.0 / 120, 200, 1.9, 1.0, 2.1, 0.8, Start),
            WallBox = [[-2, 0, -1.5], [2, 0, -1.5], [2, 3, -1.5]],
            Segments = [new ScanSegment(0, Start, Time(frames), [new ScanVideoPiece("video/seg0-000.mov", Start)])],
            Frames = Enumerable.Range(0, frames).Select(i => new ScanFrame(Time(i), 0, Pose(0.1 * i), Intrinsics(), "normal")).ToList(),
            Depth = depthFrames,
            Mesh = new ScanMesh("mesh/arkit.ply"),
            Files = files.Select(f => new ScanFileEntry(f.Key, f.Value.Length, Sha(f.Value))).ToList(),
        };
        return (manifest, files);
    }

    public static ScanIntrinsics Intrinsics() => new(2800, 2800, 1920, 1080);

    /// <summary>Identity rotation, the camera at (x, 1.5, 2), column-major.</summary>
    public static double[] Pose(double x) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, 1.5, 2, 1];

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static double Time(int frame) => Start + (frame / 30.0);

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
