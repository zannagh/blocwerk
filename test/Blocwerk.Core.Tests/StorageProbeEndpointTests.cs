// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Text;
using Blocwerk.Web.Endpoints;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>POST /api/probe/storage</c>: a signed-in browser's small JSON report becomes exactly one Information line
/// prefixed STORAGE-PROBE; anonymous, API-key, oversized, non-JSON and malformed reports log nothing.
/// </summary>
public sealed class StorageProbeEndpointTests
{
    private const string Valid =
        """{"run":"T-1021-1432-k3f9","ctx":"tab","seq":3,"step":"ladder.cache.write.100","result":"start","data":{"sizeMb":100}}""";

    [Fact]
    public async Task ASignedInReport_IsLoggedAsOneLine_WithTheFixedPrefix()
    {
        await using var host = await StorageProbeTestHost.StartAsync();

        var response = await host.PostAsync("person", Json(Valid));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var (level, message) = Assert.Single(host.Logs.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal(
            """STORAGE-PROBE run=T-1021-1432-k3f9 ctx=tab seq=3 step=ladder.cache.write.100 result=start data={"sizeMb":100}""",
            message);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("personal-write-key", HttpStatusCode.NotFound)]
    public async Task AnonymousAndApiKeyCallers_AreRefused_AndNothingIsLogged(string? who, HttpStatusCode expected)
    {
        await using var host = await StorageProbeTestHost.StartAsync();

        var response = await host.PostAsync(who, Json(Valid));

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(host.Logs.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABodyOverTheCap_Is413_WithOrWithoutAContentLength(bool announced)
    {
        await using var host = await StorageProbeTestHost.StartAsync();
        var padding = new string('x', StorageProbeEndpoints.MaxBodyBytes);
        var body = """{"run":"r","ctx":"tab","seq":1,"step":"s","result":"ok","data":{"pad":"PAD"}}""".Replace("PAD", padding);
        HttpContent content = announced
            ? Json(body)
            : new StreamContent(new UnknownLengthStream(Encoding.UTF8.GetBytes(body)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await host.PostAsync("person", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(host.Logs.Entries);
    }

    [Fact]
    public async Task ABodyJustUnderTheCap_IsLogged()
    {
        await using var host = await StorageProbeTestHost.StartAsync();
        var shell = """{"run":"r","ctx":"tab","seq":1,"step":"s","result":"ok","data":{"pad":""}}""";
        var body = shell.Replace("\"pad\":\"\"", "\"pad\":\"" + new string('x', StorageProbeEndpoints.MaxBodyBytes - shell.Length) + "\"");

        var response = await host.PostAsync("person", Json(body));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(host.Logs.Entries);
    }

    [Fact]
    public async Task ANonJsonBody_Is415()
    {
        await using var host = await StorageProbeTestHost.StartAsync();

        var response = await host.PostAsync("person", new StringContent(Valid, Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(host.Logs.Entries);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"run":"r","ctx":"tab","step":"s"}""")]
    [InlineData("""{"run":"r\nSTORAGE-PROBE forged","ctx":"tab","step":"s","result":"ok"}""")]
    [InlineData("""{"run":"r","ctx":"tab two","step":"s","result":"ok"}""")]
    public async Task AMalformedReport_Is400_AndNothingIsLogged(string body)
    {
        await using var host = await StorageProbeTestHost.StartAsync();

        var response = await host.PostAsync("person", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Logs.Entries);
    }

    [Fact]
    public async Task Data_IsLoggedCompactly_SoAReportStaysOneLine()
    {
        await using var host = await StorageProbeTestHost.StartAsync();
        var body = "{\"run\":\"r\",\"ctx\":\"standalone\",\"seq\":1,\"step\":\"quick\",\"result\":\"ok\",\n\"data\":{\n  \"ua\": \"Mozilla\\nfake line\"\n}}";

        await host.PostAsync("person", Json(body));

        var (_, message) = Assert.Single(host.Logs.Entries);
        Assert.DoesNotContain('\n', message);
        Assert.EndsWith("""data={"ua":"Mozilla\nfake line"}""", message);
    }

    [Fact]
    public void Reports_AreRateLimitedPerUser_InASlidingWindow()
    {
        var key = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < StorageProbeEndpoints.MaxReports; i++)
        {
            Assert.True(StorageProbeEndpoints.Admit(key, now));
        }

        Assert.False(StorageProbeEndpoints.Admit(key, now));
        Assert.True(StorageProbeEndpoints.Admit(Guid.NewGuid().ToString(), now));
        Assert.True(StorageProbeEndpoints.Admit(key, now + StorageProbeEndpoints.Window + TimeSpan.FromSeconds(1)));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
