// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The runner envelope contract against the other side's real output (docker/compute-jobs-protocol.md). The files in
/// <c>docker/compute-jobs-py/tests/contract</c> are checked in: <c>envelope-*.zip</c> are written by this server (this test fails
/// when the writer stops producing exactly them; the runner's pytest unpacks them), <c>result-*.zip</c> are written by the
/// Python runner (this test validates and reads them; its pytest fails when its writer drifts). A change to either writer
/// regenerates them with <c>BLOCWERK_UPDATE_CONTRACT=1</c> on both sides, committed together.
/// </summary>
public class GpuRunnerContractTests : IDisposable
{
    private const string Update = "BLOCWERK_UPDATE_CONTRACT";
    private const string Jpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAA0JCgsKCA0LCgsODg0PEyAVExISEyccHhcgLikxMC4pLSwzOko+MzZGNywtQFdBRkxOUlNSMj5aYVpQYEpRUk//2wBDAQ4ODhMREyYVFSZPNS01T09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0//wAARCAAwAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDLooorkOoKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigD//2Q==";

    private const string Prepared =
        "{\"version\":2,\"quality\":\"high\",\"options\":{\"quality\":\"high\",\"maxSteps\":null,\"maxImageEdge\":null,\"matcher\":\"auto\",\"cropMarginMm\":400.0,\"spz\":true,\"colourMatch\":true,\"cleanup\":false,\"wallZones\":false},\"geometry\":null,\"photoCentres\":{\"p01\":[0,0,0],\"p02\":[1,0,0]},\"frameCentres\":{\"vf_0001\":[0,1,0]},\"photoStems\":[\"p01\",\"p02\",\"p03\"],\"frameStems\":[\"vf_0001\"],\"points\":1200,\"meanReprojErrorPx\":0.7,\"matcher\":\"exhaustive\",\"cameraGroups\":1,\"sfm\":{\"memoryBudgetMb\":6000,\"memoryRetries\":[]},\"stageSeconds\":{\"ingest\":1.0},\"zones\":null}";

    private const string Geometry =
        "{\"cameras\":[{\"image\":\"IMG_0001\",\"R\":[1,0,0,0,1,0,0,0,1],\"t\":[0,0,0]}],\"segments\":[]}";

    private readonly string dir = Path.Combine(Path.GetTempPath(), "blocwerk-contract-tests", Guid.NewGuid().ToString("N"));

    public GpuRunnerContractTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Theory]
    [InlineData("splat-prepare", "prepare")]
    [InlineData("splat-finish", "finish")]
    public async Task TheEnvelopeTheServerWrites_IsTheCheckedInGolden_TheRunnerUnpacks(string kind, string name)
    {
        using var output = new MemoryStream();
        await RunnerComputeEnvelope.WriteAsync(output, kind, await PartsAsync(kind), CancellationToken.None);
        var golden = Path.Combine(ContractDirectory(), $"envelope-{name}.zip");
        if (Environment.GetEnvironmentVariable(Update) == "1")
        {
            await File.WriteAllBytesAsync(golden, output.ToArray());
        }

        var checkedIn = Entries(await File.ReadAllBytesAsync(golden));
        Assert.Equal(checkedIn, Entries(output.ToArray()));
        Assert.Equal(RunnerComputeEnvelope.ManifestName, checkedIn[0].Name);
        Assert.All(checkedIn, e => Assert.True(e.Stored, e.Name));
    }

    [Theory]
    [InlineData("prepare", "bundle.zip,prepared.json,sparse.zip")]
    [InlineData("finish", "wall.splat,frame.json")]
    public void TheResultThePythonRunnerWrites_IsAccepted_AndReadBackAsTheWorkersStatus(string name, string files)
    {
        var path = Path.Combine(ContractDirectory(), $"result-{name}.zip");

        RunnerComputeResult.Validate(path, 1_000_000);
        var status = RunnerComputeResult.ToStatus(path, "runner:golden");

        Assert.Equal(("runner:golden", ComputeJobStates.Succeeded), (status.JobId, status.Status));
        var listed = status.Result!.Value.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray();
        Assert.Equal(files.Split(','), listed);
        Assert.Equal(JsonValueKind.String, status.Result!.Value.GetProperty("quality").ValueKind);
    }

    private async Task<ComputeJobPart[]> PartsAsync(string kind)
    {
        var jpeg = Convert.FromBase64String(Jpeg);
        if (kind == "splat-prepare")
        {
            return
            [
                ComputeJobPart.File("photos", "IMG_0001.jpg", jpeg, "image/jpeg"),
                ComputeJobPart.File("photos", "IMG_0002.jpg", jpeg, "image/jpeg"),
                ComputeJobPart.File("photos", "IMG_0003.jpg", jpeg, "image/jpeg"),
                ComputeJobPart.File("photos", "vf_0001.jpg", jpeg, "image/jpeg"),
                ComputeJobPart.File("photos", "a00.jpg", jpeg, "image/jpeg"),
                ComputeJobPart.Json("geometry", Geometry),
                ComputeJobPart.Json("options", "{\"quality\":\"high\"}"),
            ];
        }

        var scene = Path.Combine(dir, "splat.ply");
        await File.WriteAllBytesAsync(scene, "ply\n"u8.ToArray());
        return
        [
            ComputeJobPart.Json("prepared", Prepared),
            ComputeJobPart.Json("trainStats", "{\"steps\":7000}"),
            ComputeJobPart.FromDisk("splat", "splat.ply", scene, "application/octet-stream"),
        ];
    }

    /// <summary>Name, stored?, content of every entry in order; what two zips must agree on (not their timestamps or headers).</summary>
    private static List<(string Name, bool Stored, string Content)> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        return archive.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return (e.FullName, e.Length == e.CompressedLength, Convert.ToBase64String(copy.ToArray()));
        }).ToList();
    }

    private static string ContractDirectory()
    {
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
        {
            var path = Path.Combine(parent.FullName, "docker", "compute-jobs-py", "tests", "contract");
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        throw new DirectoryNotFoundException("docker/compute-jobs-py/tests/contract not found above the test binaries");
    }
}
