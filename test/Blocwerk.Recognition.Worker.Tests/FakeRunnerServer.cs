// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Recognition.Worker.Protocol;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>The app's runner API as far as the worker calls it, answered in memory; records every call.</summary>
internal sealed class FakeRunnerServer : HttpMessageHandler
{
    private readonly List<(string Method, string Path, byte[] Body)> calls = [];

    public string HelloAnswer { get; set; } = ContractFixtures.Text("runner/hello-response/valid/recognition-server.json");

    public Queue<RunnerClaim> Claims { get; } = new();

    public byte[] Bundle { get; set; } = [];

    public HttpStatusCode ProgressStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Gets or sets what runs on every claim call (e.g. stopping the loop once the queue is empty).</summary>
    public Action? OnClaim { get; set; }

    public IReadOnlyList<(string Method, string Path, byte[] Body)> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public static RunnerClaim ClaimFor(byte[] bundle, string kind, int leaseSeconds = 30) =>
        new(Guid.NewGuid(), leaseSeconds, bundle.Length, Convert.ToHexStringLower(SHA256.HashData(bundle)), kind);

    public RunnerApiClient Client(WorkerOptions options) => new(new HttpClient(this), options);

    public byte[]? Body(string pathSuffix) => Calls.LastOrDefault(c => c.Path.EndsWith(pathSuffix, StringComparison.Ordinal)).Body;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        lock (calls)
        {
            calls.Add((request.Method.Method, path, body));
        }

        return path switch
        {
            "/api/runners/hello" => Json(HttpStatusCode.OK, HelloAnswer),
            "/api/runners/claim" => Claim(),
            _ when path.EndsWith("/bundle", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bundle) },
            _ when path.EndsWith("/progress", StringComparison.Ordinal) => Json(ProgressStatus, """{"reason":"over"}"""),
            _ => Json(HttpStatusCode.OK, """{"ok":true}"""),
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private HttpResponseMessage Claim()
    {
        OnClaim?.Invoke();
        lock (calls)
        {
            return Claims.TryDequeue(out var claim)
                ? Json(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(claim, WorkerJson.Options))
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
