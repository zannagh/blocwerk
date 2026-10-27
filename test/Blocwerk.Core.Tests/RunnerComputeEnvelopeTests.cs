// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The runner envelope: what a runner downloads (<c>parts.json</c> + stored files, in the documented shape) and what it uploads
/// (<c>status.json</c> + the files it lists; everything else, a path, a compressed file or a zip that inflates past the cap refused).
/// </summary>
public class RunnerComputeEnvelopeTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "blocwerk-envelope-tests", Guid.NewGuid().ToString("N"));

    public RunnerComputeEnvelopeTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public async Task Write_ProducesTheDocumentedManifest_AndStoresFilesUncompressed()
    {
        var onDisk = Path.Combine(dir, "big.bin");
        await File.WriteAllBytesAsync(onDisk, new byte[10_000]);
        ComputeJobPart[] parts =
        [
            ComputeJobPart.File("photos", "IMG 1 (a).jpg", [1, 2, 3], "image/jpeg"),
            ComputeJobPart.FromDisk("splat", "splat.ply", onDisk, "application/octet-stream"),
            ComputeJobPart.Json("prepared", "{\"version\":2,\"n\":1.50}"),
        ];
        using var output = new MemoryStream();

        await RunnerComputeEnvelope.WriteAsync(output, "splat-finish", parts, CancellationToken.None);

        using var zip = new ZipArchive(new MemoryStream(output.ToArray()));
        Assert.Equal(new[] { "parts.json", "IMG 1 (a).jpg", "splat.ply" }, zip.Entries.Select(e => e.FullName).ToArray());
        Assert.All(zip.Entries, e => Assert.Equal(e.Length, e.CompressedLength));
        using var manifest = JsonDocument.Parse(zip.GetEntry("parts.json")!.Open());
        var root = manifest.RootElement;
        Assert.Equal((1, "splat", "splat-finish"), (root.GetProperty("version").GetInt32(), root.GetProperty("service").GetString(), root.GetProperty("kind").GetString()));
        var listed = root.GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal(3, listed.Count);
        Assert.Equal(("photos", "IMG 1 (a).jpg"), (listed[0].GetProperty("name").GetString(), listed[0].GetProperty("file").GetString()));
        Assert.Equal(("splat", "splat.ply"), (listed[1].GetProperty("name").GetString(), listed[1].GetProperty("file").GetString()));
        Assert.Equal("prepared", listed[2].GetProperty("name").GetString());
        Assert.False(listed[2].TryGetProperty("file", out _));
        Assert.Equal(2, listed[2].GetProperty("json").GetProperty("version").GetInt32());
        Assert.Equal(10_000, zip.GetEntry("splat.ply")!.Length);
    }

    [Theory]
    [InlineData("../evil.jpg")]
    [InlineData("a/b.jpg")]
    [InlineData("a\\b.jpg")]
    [InlineData("..")]
    [InlineData("")]
    public async Task Write_RefusesAFileNameThatIsNotPlain(string name)
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(
            () => RunnerComputeEnvelope.WriteAsync(output, "splat-prepare", [ComputeJobPart.File("photos", name, [1], "image/jpeg")], CancellationToken.None));
    }

    [Fact]
    public async Task Write_RefusesTwoFilesWithOneName_AndTheManifestsName()
    {
        using var output = new MemoryStream();
        var twice = new[] { ComputeJobPart.File("photos", "a.jpg", [1], "image/jpeg"), ComputeJobPart.File("photos", "A.JPG", [2], "image/jpeg") };
        await Assert.ThrowsAsync<InvalidDataException>(() => RunnerComputeEnvelope.WriteAsync(output, "splat-prepare", twice, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => RunnerComputeEnvelope.WriteAsync(output, "splat-prepare", [ComputeJobPart.File("x", "parts.json", [1], "image/jpeg")], CancellationToken.None));
    }

    [Fact]
    public async Task Store_RefusesPartsOverTheCapBeforeWriting_AndReturnsSizeAndHash()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        ComputeJobPart[] parts = [ComputeJobPart.File("photos", "a.jpg", new byte[5000], "image/jpeg")];

        await Assert.ThrowsAsync<CaptureFailedException>(() => RunnerComputeEnvelope.StoreAsync(f.Files, "splat-prepare", parts, 1000, CancellationToken.None));
        Assert.Empty(f.Files.ListFiles());

        var (stored, bytes, sha) = await RunnerComputeEnvelope.StoreAsync(f.Files, "splat-prepare", parts, 1_000_000, CancellationToken.None);
        Assert.Equal(new FileInfo(f.Files.ResolvePhysicalPath(stored)!).Length, bytes);
        Assert.Equal(64, sha.Length);
    }

    [Fact]
    public void AValidResult_ReadsBackAsTheWorkersStatus()
    {
        var path = Save(SplatJobSupport.PrepareResult());

        RunnerComputeResult.Validate(path, 1_000_000);
        var status = RunnerComputeResult.ToStatus(path, "runner:abc");

        Assert.Equal(("runner:abc", ComputeJobStates.Succeeded), (status.JobId, status.Status));
        Assert.Equal(3, status.Result!.Value.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void AStatusMayListItsFilesAsPlainStrings()
    {
        var zip = SplatJobSupport.ResultZip(
            [], [("wall.splat", [1])], status: "{\"status\":\"succeeded\",\"result\":{\"files\":[\"wall.splat\"]}}"u8.ToArray());

        RunnerComputeResult.Validate(Save(zip), 1_000_000);
    }

    [Fact]
    public void ARefusedResult_IsNeverReadAsOne()
    {
        var ok = ("wall.splat", new byte[8]);
        (string Why, byte[] Zip)[] refused =
        [
            ("not a zip", [1, 2, 3, 4]),
            ("path traversal entry", SplatJobSupport.ResultZip(["wall.splat"], [ok, ("../evil.json", [1])])),
            ("nested entry", SplatJobSupport.ResultZip(["wall.splat"], [ok, ("sub/x.json", [1])])),
            ("absolute entry", SplatJobSupport.ResultZip(["wall.splat"], [ok, ("/etc/x.json", [1])])),
            ("traversal in the status", SplatJobSupport.ResultZip(["../wall.splat"], [ok])),
            ("a file the status does not list", SplatJobSupport.ResultZip(["wall.splat"], [ok, ("extra.json", [1])])),
            ("a listed file that is missing", SplatJobSupport.ResultZip(["wall.splat", "frame.json"], [ok])),
            ("a file type outside the allow-list", SplatJobSupport.ResultZip(["wall.splat", "run.sh"], [ok, ("run.sh", [1])])),
            ("a status that lists itself", SplatJobSupport.ResultZip(["status.json", "wall.splat"], [ok])),
            ("no files listed", SplatJobSupport.ResultZip([], [])),
            ("a failed status", SplatJobSupport.ResultZip(["wall.splat"], [ok], status: SplatJobSupport.StatusJson("failed", "wall.splat"))),
            ("a status that is not JSON", SplatJobSupport.ResultZip(["wall.splat"], [ok], status: [1, 2, 3])),
            ("no status", ZipOf(("wall.splat", [1]))),
            ("a compressed file", SplatJobSupport.ResultZip(["wall.splat"], [("wall.splat", new byte[100_000])], deflated: ["wall.splat"])),
            ("a listed name twice in the zip", ZipOf(("status.json", SplatJobSupport.StatusJson("succeeded", "wall.splat")), ("wall.splat", [1]), ("wall.splat", [2]))),
        ];

        foreach (var (why, zip) in refused)
        {
            Assert.True(Throws(Save(zip, why.Replace(' ', '_')), 1_000_000), why);
        }
    }

    [Fact]
    public void AZipBomb_IsRefused_ByTheTotalCapAndByItsCompression()
    {
        // A stored file past the total cap is refused ...
        var honest = SplatJobSupport.ResultZip(["wall.splat"], [("wall.splat", new byte[3_000_000])]);
        Assert.True(Throws(Save(honest, "honest"), 1_000_000));
        Assert.False(Throws(Save(honest, "honest-ok"), 4_000_000));

        // ... and 20 MB of zeros deflate to ~20 KB on the wire, far under any cap: refused for not being stored.
        var bomb = DeflatedBomb();
        Assert.True(Throws(Save(bomb, "bomb"), 1L << 40));
    }

    [Fact]
    public void ATooLargeOrTooManyFileResult_IsRefused()
    {
        var many = Enumerable.Range(0, 70).Select(i => ($"f{i}.json", new byte[] { 1 })).ToArray();
        Assert.True(Throws(Save(SplatJobSupport.ResultZip(many.Select(m => m.Item1).ToArray(), many), "many"), 1_000_000));

        var hugeStatus = SplatJobSupport.StatusJson("succeeded", "wall.splat").Concat(new byte[5 * 1024 * 1024]).ToArray();
        Assert.True(Throws(Save(SplatJobSupport.ResultZip(["wall.splat"], [("wall.splat", [1])], status: hugeStatus), "huge"), 1L << 40));
    }

    private static byte[] DeflatedBomb() =>
        SplatJobSupport.ResultZip(["wall.splat"], [("wall.splat", new byte[20_000_000])], deflated: ["wall.splat"]);

    private static bool Throws(string path, long maxBytes)
    {
        try
        {
            RunnerComputeResult.Validate(path, maxBytes);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static byte[] ZipOf(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(bytes);
            }
        }

        return output.ToArray();
    }

    private string Save(byte[] bytes, string name = "result")
    {
        var path = Path.Combine(dir, name + ".zip");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
