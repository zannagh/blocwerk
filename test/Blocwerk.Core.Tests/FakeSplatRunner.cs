// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A 3D runner that prepares, trains and finishes (capabilities <c>splat</c>, <c>splat-prepare</c>, <c>splat-finish</c>): it
/// claims the next job, reads a prepare / finish envelope the way the Python runner does (<c>parts.json</c> first, every file
/// stored) and uploads what the splat worker would have produced.
/// </summary>
internal sealed class FakeSplatRunner(GpuJobQueue queue, GpuRunner runner)
{
    /// <summary>The runner's id.</summary>
    public Guid Id => runner.Id;

    /// <summary>The jobs taken, with the part names of their envelope (empty for a training job).</summary>
    public List<(GpuJobKind Kind, string[] Parts)> Taken { get; } = [];

    /// <summary>The <c>sparse.zip</c> a prepare returns (a markerless reconstruction needs a readable one).</summary>
    public byte[] Sparse { get; set; } = [1, 2];

    /// <summary>A runner of the wall's owner, online now, serving <paramref name="wallId"/>.</summary>
    public static async Task<FakeSplatRunner> AddAsync(WallTestHarness h, GpuJobQueue queue, Guid wallId)
    {
        var (token, prefix) = GpuRunnerTokens.Create();
        var runner = new GpuRunner
        {
            Name = "cellar", OwnerUserId = h.Owner.Id, KeyHash = GpuRunnerTokens.Hash(token), KeyPrefix = prefix,
            LastSeenAt = queue.Clock.GetUtcNow(), MaxQuality = "max", MemoryBudgetMb = 16000,
            Capabilities = RunnerCapabilities.Store(SplatJobSupport.PrepareAndFinish),
        };
        await using var db = h.CreateContext();
        db.GpuRunners.Add(runner);
        db.GpuRunnerWalls.Add(new GpuRunnerWall { RunnerId = runner.Id, WallId = wallId });
        await db.SaveChangesAsync();
        return new FakeSplatRunner(queue, runner);
    }

    /// <summary>Claims the next job, checks its envelope, uploads its result; returns the job.</summary>
    public async Task<GpuJob> DeliverAsync(GpuJobKind expected)
    {
        var job = await queue.TryClaimAsync(runner, null, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(expected, job.Kind);
        var body = job.Kind switch
        {
            GpuJobKind.SplatPrepare => await PrepareAsync(job),
            GpuJobKind.SplatFinish => await FinishAsync(job),
            _ => RunnerFixture.Gzip(RunnerFixture.SlimPly()),
        };
        var encoding = job.Kind == GpuJobKind.Splat ? "gzip" : null;
        Assert.Equal(RunnerJobOutcome.Ok, await queue.AcceptResultAsync(runner, job.Id, new MemoryStream(body), encoding, null, CancellationToken.None));
        return job;
    }

    private async Task<byte[]> PrepareAsync(GpuJob job)
    {
        var parts = await ReadEnvelopeAsync(job);
        Assert.Contains("photos", parts);
        Assert.Contains("options", parts);
        return SplatJobSupport.ResultZip(
            [WallCaptureProcessor.BundleFile, WallCaptureProcessor.PreparedFile, WallCaptureProcessor.SparseFile],
            [
                (WallCaptureProcessor.BundleFile, RunnerFixture.Bundle()),
                (WallCaptureProcessor.PreparedFile, "{\"version\":1}"u8.ToArray()),
                (WallCaptureProcessor.SparseFile, Sparse),
            ]);
    }

    private async Task<byte[]> FinishAsync(GpuJob job)
    {
        var parts = await ReadEnvelopeAsync(job);
        Assert.Equal(new[] { "prepared", "trainStats", "splat" }, parts);
        return SplatJobSupport.ResultZip(
            [CaptureSplatDocuments.SpzFile, CaptureSplatDocuments.FrameFile],
            [
                (CaptureSplatDocuments.SpzFile, FakeComputeJobClient.Gzip(new byte[4096])),
                (CaptureSplatDocuments.FrameFile, System.Text.Encoding.UTF8.GetBytes(FakeComputeJobClient.SplatFrame(aligned: true))),
            ]);
    }

    /// <summary>The part names of the job's envelope, after checking it is one: manifest first, every file entry stored.</summary>
    private async Task<string[]> ReadEnvelopeAsync(GpuJob job)
    {
        var (outcome, path) = await queue.BundleForRunnerAsync(runner, job.Id, CancellationToken.None);
        Assert.Equal(RunnerJobOutcome.Ok, outcome);
        using var zip = ZipFile.OpenRead(path!);
        Assert.Equal(RunnerComputeEnvelope.ManifestName, zip.Entries[0].FullName);
        Assert.All(zip.Entries, e => Assert.Equal(e.Length, e.CompressedLength));
        await using var manifest = zip.Entries[0].Open();
        var root = JsonNode.Parse(manifest)!;
        Assert.Equal(RunnerCapabilities.KindName(job.Kind), root["kind"]!.GetValue<string>());
        var parts = root["parts"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToArray();
        Taken.Add((job.Kind, parts));
        return parts;
    }
}
