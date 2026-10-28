// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Builders for the splat prepare / finish runner jobs: hellos, queued jobs with an envelope, result zips.</summary>
internal static class SplatJobSupport
{
    public static readonly string[] PrepareAndFinish = [RunnerCapabilities.Splat, RunnerCapabilities.SplatPrepare, RunnerCapabilities.SplatFinish];

    /// <summary>The hello of a runner with <paramref name="budgetMb"/> that names <paramref name="capabilities"/>.</summary>
    public static RunnerHello Hello(int budgetMb, params string[] capabilities) =>
        new("1.0", "RTX", 24000, "max", budgetMb, "Linux x86_64", null, "gsplat", true, false, capabilities, null);

    /// <summary>Says hello for <paramref name="runner"/> and returns its stored row.</summary>
    public static async Task<GpuRunner> HelloAsync(RunnerFixture f, GpuRunner runner, int budgetMb, params string[] capabilities)
    {
        await f.Queue.HelloAsync(runner, Hello(budgetMb, capabilities), CancellationToken.None);
        await using var db = f.Harness.CreateContext();
        return await db.GpuRunners.AsNoTracking().SingleAsync(r => r.Id == runner.Id);
    }

    /// <summary>A queued prepare / finish job of a new capture (a markerless one when <paramref name="markerless"/>), with a real envelope.</summary>
    public static async Task<GpuJob> AddJobAsync(
        RunnerFixture f, Guid wallId, GpuJobKind kind, bool markerless = false, WallCaptureStatus capture = WallCaptureStatus.Succeeded, int? requiredMb = null)
    {
        await using var db = f.Harness.CreateContext();
        var row = new WallCapture
        {
            WallId = wallId, CreatedByUserId = f.Harness.Owner.Id, Status = capture, Stage = "Done",
            GeometryMode = markerless ? WallCaptureGeometryMode.Features : WallCaptureGeometryMode.Markers,
        };
        db.WallCaptures.Add(row);
        await db.SaveChangesAsync();
        var envelope = await RunnerComputeEnvelope.StoreAsync(
            f.Files, RunnerCapabilities.KindName(kind),
            [ComputeJobPart.File("photos", "p01.jpg", CaptureScenario.TinyJpeg(), "image/jpeg"), ComputeJobPart.Json("options", "{\"quality\":\"high\"}")],
            long.MaxValue, CancellationToken.None);
        return await f.Queue.EnqueueAsync(
            new GpuJob
            {
                WallId = wallId, CaptureId = row.Id, GeometryModelId = Guid.NewGuid(), Kind = kind, Quality = SplatQuality.Ultra, RequiredMemoryMb = requiredMb,
                BundlePath = envelope.Stored, BundleBytes = envelope.Bytes, BundleSha256 = envelope.Sha256, PreparedPath = envelope.Stored,
            },
            CancellationToken.None);
    }

    /// <summary>The <c>status.json</c> a runner uploads: the splat worker's job status, listing <paramref name="listed"/>.</summary>
    public static byte[] StatusJson(string status = "succeeded", params string[] listed) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["jobId"] = "worker-job", ["kind"] = "splat-prepare", ["status"] = status, ["progress"] = 1.0,
            ["result"] = new JsonObject { ["files"] = new JsonArray(listed.Select(n => (JsonNode)new JsonObject { ["name"] = n, ["url"] = $"/v1/jobs/x/files/{n}" }).ToArray()) },
        }.ToJsonString());

    /// <summary>A result zip: <c>status.json</c> listing <paramref name="listed"/> plus <paramref name="entries"/>, stored unless named in <paramref name="deflated"/>.</summary>
    public static byte[] ResultZip(string[] listed, (string Name, byte[] Bytes)[] entries, string[]? deflated = null, byte[]? status = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, RunnerComputeResult.StatusName, status ?? StatusJson("succeeded", listed), CompressionLevel.Optimal);
            foreach (var (name, bytes) in entries)
            {
                Add(zip, name, bytes, deflated?.Contains(name) == true ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            }
        }

        return output.ToArray();
    }

    /// <summary>A valid prepare result: the bundle, its state and the sparse model.</summary>
    public static byte[] PrepareResult(int bundleBytes = 64) =>
        ResultZip(["bundle.zip", "prepared.json", "sparse.zip"], [("bundle.zip", new byte[bundleBytes]), ("prepared.json", "{}"u8.ToArray()), ("sparse.zip", [1, 2])]);

    /// <summary>A valid finish result.</summary>
    public static byte[] FinishResult(int splatBytes = 64) =>
        ResultZip(["wall.splat", "frame.json"], [("wall.splat", new byte[splatBytes]), ("frame.json", "{}"u8.ToArray())]);

    /// <summary>The job row as stored now.</summary>
    public static async Task<GpuJob> JobAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.GpuJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes, CompressionLevel level)
    {
        using var stream = zip.CreateEntry(name, level).Open();
        stream.Write(bytes);
    }
}
