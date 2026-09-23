using System.Net;
using System.Text;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The protocol-v1 HTTP client: bearer key on authenticated calls only, the key never in a message,
/// and every HTTP / network failure mapped to an admin-friendly <see cref="ComputeJobException"/>.
/// </summary>
public class ComputeJobClientTests
{
    private const string Key = "k3y-that-must-never-leak";

    [Fact]
    public async Task Submit_SendsTheBearerKey_AndReturnsTheJobId()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Accepted, """{"jobId":"abc","status":"queued"}"""));
        var id = await Client(handler).SubmitJsonAsync("solve", "{}", CancellationToken.None);

        Assert.Equal("abc", id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://geometry:8000/v1/jobs/solve", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Key, request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Health_IsNeverAuthenticated()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"status":"ok","kinds":["solve","textures"]}"""));
        var health = await Client(handler).GetHealthAsync(CancellationToken.None);

        Assert.Equal(["solve", "textures"], health.Kinds);
        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ComputeFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ComputeFailureKind.Rejected)]
    [InlineData((HttpStatusCode)429, ComputeFailureKind.Busy)]
    [InlineData(HttpStatusCode.InternalServerError, ComputeFailureKind.Unavailable)]
    public async Task HttpErrors_MapToFriendlyFailures_WithoutTheKey(HttpStatusCode status, ComputeFailureKind kind)
    {
        var handler = new StubHandler(_ => Json(status, """{"detail":"markers: need focal35mm"}"""));
        var ex = await Assert.ThrowsAsync<ComputeJobException>(
            () => Client(handler).SubmitJsonAsync("solve", "{}", CancellationToken.None));

        Assert.Equal(kind, ex.Kind);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task UnknownJob_OnStatus_IsJobNotFound()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, """{"detail":"no such job"}"""));
        var ex = await Assert.ThrowsAsync<ComputeJobException>(
            () => Client(handler).GetStatusAsync("gone", CancellationToken.None));
        Assert.Equal(ComputeFailureKind.JobNotFound, ex.Kind);
    }

    [Fact]
    public async Task Timeout_AndNetworkErrors_AreTransient()
    {
        var slow = new StubHandler(_ => throw new TaskCanceledException("timeout"));
        var down = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var timeout = await Assert.ThrowsAsync<ComputeJobException>(() => Client(slow).GetStatusAsync("a", CancellationToken.None));
        var refused = await Assert.ThrowsAsync<ComputeJobException>(() => Client(down).GetStatusAsync("a", CancellationToken.None));

        Assert.True(timeout.IsTransient);
        Assert.True(refused.IsTransient);
    }

    [Fact]
    public async Task NoUrl_IsNotConfigured_AndNeverCallsOut()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = Client(handler, url: string.Empty);

        Assert.False(client.IsConfigured);
        var ex = await Assert.ThrowsAsync<ComputeJobException>(() => client.SubmitJsonAsync("solve", "{}", CancellationToken.None));
        Assert.Equal(ComputeFailureKind.NotConfigured, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Settings_BindFromTheEnvStyleConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blocwerk:GeometryService:Url"] = "http://wall-geometry:8000",
                ["Blocwerk:GeometryService:ApiKey"] = "x",
                ["Blocwerk:GeometryService:JobTimeoutMinutes"] = "7",
            })
            .Build();
        var settings = new BlocwerkSettings(config);

        Assert.True(settings.GeometryService.IsConfigured);
        Assert.Equal(TimeSpan.FromMinutes(7), settings.GeometryService.JobTimeout);
        Assert.False(settings.SplatService.IsConfigured);
    }

    [Theory]
    [InlineData("https://splat.example.ts.net", true)]
    [InlineData("http://wall-geometry:8000", true)]
    [InlineData("http://localhost:8100", true)]
    [InlineData("http://127.0.0.1:8100", true)]
    [InlineData("http://splat.example.ts.net", false)]
    [InlineData("http://10.0.0.5:8100", false)]
    [InlineData("ftp://wall-geometry", false)]
    public void PlainHttp_IsOnlyAcceptedToLocalHosts(string url, bool configured)
    {
        var settings = new ComputeServiceSettings { Url = url };

        Assert.Equal(configured, settings.IsConfigured);
        Assert.Equal(configured, settings.ConfigurationError is null);
    }

    [Fact]
    public async Task InsecureUrl_IsRefused_WithTheReason_AndNeverCallsOut()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = Client(handler, url: "http://geometry.example.com:8000");

        var ex = await Assert.ThrowsAsync<ComputeJobException>(() => client.SubmitJsonAsync("solve", "{}", CancellationToken.None));

        Assert.Equal(ComputeFailureKind.NotConfigured, ex.Kind);
        Assert.Contains("https://", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_AboveTheLimit_IsRefused()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) });
        var client = Client(handler);

        var ex = await Assert.ThrowsAsync<ComputeJobException>(() => client.DownloadFileAsync("j", "facet_0.jpg", 1024, CancellationToken.None));
        Assert.Equal(ComputeFailureKind.Protocol, ex.Kind);
        Assert.Equal(4096, (await client.DownloadFileAsync("j", "facet_0.jpg", 4096, CancellationToken.None)).Length);
    }

    [Fact]
    public async Task OverlongJobId_IsAProtocolError()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Accepted, $$"""{"jobId":"{{new string('a', 129)}}"}"""));

        var ex = await Assert.ThrowsAsync<ComputeJobException>(() => Client(handler).SubmitJsonAsync("solve", "{}", CancellationToken.None));

        Assert.Equal(ComputeFailureKind.Protocol, ex.Kind);
    }

    [Fact]
    public async Task WorkerDetail_IsShortenedAndPathFree()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.UnprocessableEntity, """{"detail":"photo p01 unreadable at /srv/jobs/9/p01.jpg"}"""));

        var ex = await Assert.ThrowsAsync<ComputeJobException>(() => Client(handler).SubmitJsonAsync("solve", "{}", CancellationToken.None));

        Assert.Contains("photo p01 unreadable", ex.Message);
        Assert.DoesNotContain("/srv/jobs", ex.Message);
    }

    private static ComputeJobClient Client(StubHandler handler, string url = "http://geometry:8000") => new(
        ComputeServiceKind.Geometry,
        new HttpClient(handler),
        new ComputeServiceSettings { Url = url, ApiKey = Key },
        NullLogger<ComputeJobClient>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
