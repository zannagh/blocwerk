// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Capture.Scan;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>
/// The real routes over the real service, in memory: a personal write key uploads a package file by file (names with
/// folders), resumes after an interruption and commits; a wall key, a kiosk key, a read-only key and nobody are refused.
/// </summary>
public class WallScanHttpTests
{
    [Fact]
    public async Task APersonalWriteKey_UploadsResumesAndCommits()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        await using var app = await StartAsync(s);
        using var client = Client(app, "personal-write-key");
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var scans = $"/api/walls/{h.WallId}/scans";

        var begun = await BeginAsync(client, scans, manifest);
        foreach (var name in begun.Missing.Take(2))
        {
            Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, $"{scans}/{begun.ScanId}/files/{name}", files[name])).StatusCode);
        }

        var resumed = await BeginAsync(client, scans, manifest);
        Assert.Equal(files.Count - 2, resumed.Missing.Count);
        foreach (var name in resumed.Missing)
        {
            Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, $"{scans}/{begun.ScanId}/files/{name}", files[name])).StatusCode);
        }

        var commit = await client.PostAsync($"{scans}/{begun.ScanId}/commit", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        var state = await client.GetFromJsonAsync<ScanUploadState>($"{scans}/{begun.ScanId}", CapturePackageFiles.Json);
        Assert.Equal(ScanUploadState.Received, state!.Status);
    }

    [Fact]
    public async Task ABadHash_Is422()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        await using var app = await StartAsync(s);
        using var client = Client(app, "personal-write-key");
        var (manifest, files) = ScanPackageBuilder.Build(h.WallId);
        var scans = $"/api/walls/{h.WallId}/scans";
        var begun = await BeginAsync(client, scans, manifest);
        var bytes = files["mesh/arkit.ply"].ToArray();
        bytes[^1] ^= 1;

        var put = await PutAsync(client, $"{scans}/{begun.ScanId}/files/mesh/arkit.ply", bytes);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Theory]
    [InlineData("wall-key", HttpStatusCode.Forbidden)]
    [InlineData("kiosk-key", HttpStatusCode.Forbidden)]
    [InlineData("read-only-personal-key", HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task OtherCallers_AreRefusedBeforeTheService(string? who, HttpStatusCode expected)
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        await using var app = await StartAsync(s);
        using var client = Client(app, who);
        var (manifest, _) = ScanPackageBuilder.Build(h.WallId);

        var begin = await client.PostAsJsonAsync($"/api/walls/{h.WallId}/scans", manifest, CapturePackageFiles.Json);

        Assert.Equal(expected, begin.StatusCode);
    }

    private static async Task<ScanUploadState> BeginAsync(HttpClient client, string scans, ScanManifest manifest)
    {
        var response = await client.PostAsJsonAsync(scans, manifest, CapturePackageFiles.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ScanUploadState>(CapturePackageFiles.Json))!;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string url, byte[] bytes) =>
        client.PutAsync(url, new ByteArrayContent(bytes));

    private static HttpClient Client(WebApplication app, string? who)
    {
        var client = app.GetTestClient();
        if (who is not null)
        {
            client.DefaultRequestHeaders.Add(HeaderApiKeyAuthHandler.Header, who);
        }

        return client;
    }

    private static async Task<WebApplication> StartAsync(ScanScenario s)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IWallScanService>(s.Service);
        builder.Services.AddWallScanRateLimit();
        builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderApiKeyAuthHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddPolicy(
            BlocwerkPolicies.HumanOrUserApiKey, HumanOrPersonalApiKeyPolicy.Build(new AuthorizationPolicyBuilder())));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWallScans();
        await app.StartAsync();
        return app;
    }
}
