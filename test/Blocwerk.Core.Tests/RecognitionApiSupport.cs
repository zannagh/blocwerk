// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Runners;
using Blocwerk.Web.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Helpers for the recognition side of the runner API: the instance runner, runner keys, claims and result zips.</summary>
internal static class RecognitionApiSupport
{
    public const string Api = RunnerApiEndpoints.Prefix;

    /// <summary>The deployment's instance key in these tests (only its hash reaches the options).</summary>
    public const string InstanceKey = "instance-key-for-tests-0123456789abcdefghij";

    public static RecognitionQueueOptions Options(RecognitionMode mode = RecognitionMode.Queue, string? modelsDirectory = null, bool instanceKey = true) => new()
    {
        Mode = mode,
        InstanceKeyHash = instanceKey ? GpuRunnerTokens.Hash(InstanceKey) : null,
        ModelsDirectory = modelsDirectory ?? Path.Combine(Path.GetTempPath(), $"no-models-{Guid.NewGuid():N}"),
        WaiterPoll = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>Runs the start-up registration of the instance runner with <paramref name="options"/>; returns its row (or null).</summary>
    public static async Task<GpuRunner?> RegisterInstanceAsync(RecognitionFixture r, RecognitionQueueOptions options)
    {
        await new InstanceRunnerRegistration(r.Harness.RootContextFactory, options, NullLogger<InstanceRunnerRegistration>.Instance)
            .ApplyAsync(CancellationToken.None);
        await using var db = r.Harness.CreateContext();
        return await db.GpuRunners.AsNoTracking().SingleOrDefaultAsync(x => x.Scope == RunnerScope.Instance);
    }

    /// <summary>An owned runner on <paramref name="walls"/> (default: the harness wall) with these capabilities; its key.</summary>
    public static async Task<string> OwnedRunnerAsync(RecognitionFixture r, string capabilities, params Guid[] walls)
    {
        var (runner, key) = await r.Runners.AddRunnerAsync("pc", walls: walls.Length == 0 ? [r.Harness.WallId] : walls);
        await using var db = r.Harness.CreateContext();
        await db.GpuRunners.Where(x => x.Id == runner.Id).ExecuteUpdateAsync(
            s => s.SetProperty(x => x.Capabilities, capabilities).SetProperty(x => x.MemoryBudgetMb, 2048));
        return key;
    }

    /// <summary>A claim with an optional claim token; the answer's JSON (null on 204) and status.</summary>
    public static async Task<(int Status, JsonElement? Claim)> ClaimAsync(RunnerApiTestHost host, string key, string? token = null)
    {
        using var request = Request(HttpMethod.Post, $"{Api}/claim", key, token, new StringContent("{}", Encoding.UTF8, "application/json"));
        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body.Length == 0 ? null : JsonDocument.Parse(body).RootElement.Clone());
    }

    public static HttpRequestMessage Request(HttpMethod method, string path, string key, string? token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        if (token is not null)
        {
            request.Headers.Add(RunnerApiEndpoints.ClaimTokenHeader, token);
        }

        return request;
    }

    /// <summary>An envelope result: a zip of <c>status.json</c> (and any <paramref name="extra"/> entries).</summary>
    public static ByteArrayContent ResultZip(string statusJson, params string[] extra)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { RunnerComputeResult.StatusName }.Concat(extra))
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(name == RunnerComputeResult.StatusName ? statusJson : "{}"));
            }
        }

        return new ByteArrayContent(output.ToArray());
    }

    public static ByteArrayContent Succeeded() => ResultZip("""{"status":"succeeded","result":{"version":1,"markers":[],"rejected":0}}""");
}
