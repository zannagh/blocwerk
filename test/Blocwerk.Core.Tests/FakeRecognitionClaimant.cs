// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Recognition.Remote;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>A recognition runner played by the test: claims what the app queued and answers it, or never shows up.</summary>
internal sealed class FakeRecognitionClaimant(RecognitionFixture f, GpuRunner pod)
{
    public static async Task<FakeRecognitionClaimant> CreateAsync(RecognitionFixture f, string name = "pod") => new(f, await f.RunnerAsync(name));

    /// <summary>A fixture whose queue runs in <paramref name="mode"/>, waiters polling fast.</summary>
    public static Task<RecognitionFixture> FixtureAsync(WallTestHarness h, RecognitionMode mode) =>
        RecognitionFixture.CreateAsync(h, new RecognitionQueueOptions { Mode = mode, WaiterPoll = TimeSpan.FromMilliseconds(50) });

    public static RecognitionCaller Caller(RecognitionFixture f) => new(f.Queue, NullLogger<RecognitionCaller>.Instance);

    /// <summary>Waits until <paramref name="count"/> jobs are queued (none claimed yet); returns them oldest first.</summary>
    public static async Task<List<RecognitionJob>> QueuedAsync(RecognitionFixture f, int count = 1)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until)
        {
            await using var db = f.Harness.CreateContext();
            var queued = await db.RecognitionJobs.AsNoTracking().Where(j => j.Status == GpuJobStatus.Queued).ToListAsync();
            if (queued.Count >= count)
            {
                return queued.OrderBy(j => j.CreatedAt).ToList();
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"{count} recognition job(s) were not queued");
    }

    /// <summary>Claims the next job as the queue offers it.</summary>
    public async Task<RecognitionJob> ClaimAsync()
    {
        await QueuedAsync(f);
        return await f.Queue.TryClaimAsync(pod, CancellationToken.None) ?? throw new InvalidOperationException("Nothing to claim.");
    }

    public Task<RunnerJobOutcome> CompleteAsync(RecognitionJob job, string resultJson) =>
        f.Queue.ResultAsync(pod, job.Id, resultJson, CancellationToken.None);

    public Task<RunnerJobOutcome> FailAsync(RecognitionJob job, string reason) =>
        f.Queue.FailAsync(pod, job.Id, new RunnerFailure(reason, Retryable: false), CancellationToken.None);

    /// <summary>A <c>detect-holds@2</c> result for <c>p01.jpg</c> (100 × 100) with <paramref name="count"/> boxes in a row.</summary>
    public static string Boxes(int count)
    {
        var boxes = string.Join(',', Enumerable.Range(0, count).Select(i => $"[{45 - (i * 20)},45,{55 - (i * 20)},55,0.9,\"hold\"]"));
        return "{\"version\":1,\"photos\":{\"p01.jpg\":{\"width\":100,\"height\":100,\"boxes\":[" + boxes + "]}}}";
    }

    /// <summary>The queued detector over <paramref name="f"/>, the app's model being <paramref name="modelFile"/>.</summary>
    public static QueuedHoldDetectionService Detector(RecognitionFixture f, IHoldDetectionService inProcess, string modelFile)
    {
        var settings = new BlocwerkSettings();
        settings.HoldDetection.ModelPath = modelFile;
        var models = new RunnerHoldDetection(
            f.Runners.Queue, f.Runners.Files, f.Harness.RootContextFactory, settings, NullLogger<RunnerHoldDetection>.Instance, f.Clock);
        return new QueuedHoldDetectionService(inProcess, Caller(f), models, settings.HoldDetection);
    }

    /// <summary>The photo a <c>detect-holds@2</c> job carries as <c>p01.jpg</c>.</summary>
    public static byte[] PhotoOf(RecognitionFixture f, RecognitionJob job)
    {
        using var zip = ZipFile.OpenRead(f.Runners.Files.ResolvePhysicalPath(job.InputPath!)!);
        using var entry = zip.GetEntry("p01.jpg")!.Open();
        using var bytes = new MemoryStream();
        entry.CopyTo(bytes);
        return bytes.ToArray();
    }
}
