// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The strict content check of an uploaded splat (exact PLY layout and length, known columns, finite values, a splat
/// cap, no overflow; an .spz decompressed capped and checked to the byte) and the train.json allow-list of a bundle.
/// </summary>
public sealed class SplatResultValidationTests : IDisposable
{
    private readonly string path = Path.GetTempFileName();

    [Fact]
    public void TheSlimPly_AndAWellFormedSpz_Pass()
    {
        Assert.Equal(SplatResultFormat.Ply, Check(RunnerFixture.SlimPly(10)));
        Assert.Equal(SplatResultFormat.Spz, Check(Spz(count: 5, version: 3, shDegree: 1)));
        Assert.Equal(SplatResultFormat.Spz, Check(Spz(count: 5, version: 2, shDegree: 0)));
    }

    [Theory]
    [InlineData("element face 1\n")]
    [InlineData("element vertex 3\n")]
    [InlineData("property float secret_payload\n")]
    [InlineData("property double x2\n")]
    [InlineData("property float x\n")]
    public void Ply_WithAnUnexpectedHeaderLine_IsRefused(string extra)
    {
        var ply = Encoding.ASCII.GetString(RunnerFixture.SlimPly(3)).Replace("end_header\n", extra + "end_header\n", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Check(Encoding.Latin1.GetBytes(ply)));
    }

    [Fact]
    public void Ply_WithTrailingData_OrNaN_OrOverTheSplatCap_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => Check([.. RunnerFixture.SlimPly(3), 0, 0, 0, 0]));
        var nan = RunnerFixture.SlimPly(3);
        BinaryPrimitives.WriteSingleLittleEndian(nan.AsSpan(nan.Length - 4), float.NaN);
        Assert.Throws<InvalidDataException>(() => Check(nan));
        var inf = RunnerFixture.SlimPly(3);
        BinaryPrimitives.WriteSingleLittleEndian(inf.AsSpan(inf.Length - 8), float.PositiveInfinity);
        Assert.Throws<InvalidDataException>(() => Check(inf));
        Assert.Throws<InvalidDataException>(() => Check(RunnerFixture.SlimPly(11), maxSplats: 10));
    }

    [Fact]
    public void Ply_WhoseSizeOverflows_IsRefused_NotWrapped()
    {
        var header = Encoding.ASCII.GetString(RunnerFixture.PlyHeader(1))
            .Replace("element vertex 1\n", $"element vertex {long.MaxValue / 8}\n", StringComparison.Ordinal);
        Assert.Throws<OverflowException>(() => Check(Encoding.ASCII.GetBytes(header), maxSplats: long.MaxValue));
    }

    [Fact]
    public void Spz_ThatIsTruncated_HasTrailingData_OrInflatesPastItsHeader_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => Check(Spz(count: 5, version: 3, shDegree: 0, bodyDelta: -1)));
        Assert.Throws<InvalidDataException>(() => Check(Spz(count: 5, version: 3, shDegree: 0, bodyDelta: 1)));
        Assert.Throws<InvalidDataException>(() => Check(Spz(count: 5, version: 3, shDegree: 0), maxSplats: 4));

        // A nested bomb: one splat in the header, 64 MB of zeros behind it; refused after reading one byte too many.
        Assert.Throws<InvalidDataException>(() => Check(Spz(count: 1, version: 3, shDegree: 0, bodyDelta: 64 * 1024 * 1024)));
    }

    [Theory]
    [InlineData("{\"version\":1,\"quality\":\"max\",\"steps\":30000,\"checkpoints\":[7000,15000]}", true)]
    [InlineData("{\"version\":1,\"serverUrl\":\"https://example.org\"}", false)]
    [InlineData("{\"version\":1,\"quality\":{\"nested\":true}}", false)]
    [InlineData("[1,2,3]", false)]
    [InlineData("not json", false)]
    public void TrainJson_OnlyKnownKeysAndPlainValues_PassTheBundle(string json, bool ok)
    {
        var replaced = ReplaceTrainJson(RunnerFixture.Bundle(), json);
        if (ok)
        {
            RunnerBundle.Sanitize(replaced);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => RunnerBundle.Sanitize(replaced));
        }
    }

    public void Dispose() => File.Delete(path);

    private static byte[] Spz(int count, uint version, int shDegree, int bodyDelta = 0)
    {
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x5053474E);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)count);
        header[12] = (byte)shDegree;
        header[13] = 12;
        var body = new byte[(count * SpzFormat.BytesPerPoint(version, shDegree)) + bodyDelta];
        return RunnerFixture.Gzip([.. header, .. body]);
    }

    private static byte[] ReplaceTrainJson(byte[] bundle, string json)
    {
        using var input = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                using var target = zip.CreateEntry(entry.FullName).Open();
                if (entry.FullName == "train.json")
                {
                    target.Write(Encoding.UTF8.GetBytes(json));
                }
                else
                {
                    using var source = entry.Open();
                    source.CopyTo(target);
                }
            }
        }

        return output.ToArray();
    }

    private string Check(byte[] bytes, long maxSplats = SplatResultFormat.DefaultMaxSplats)
    {
        File.WriteAllBytes(path, bytes);
        return SplatResultFormat.Validate(path, maxSplats);
    }
}
