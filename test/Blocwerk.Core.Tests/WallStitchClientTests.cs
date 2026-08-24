using System.Net;
using System.Net.Http;
using System.Text;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Blocwerk.Core.Stitching;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Exercises the sidecar HTTP contract against a stub handler, including the one behaviour that
/// cannot be relaxed: a full-resolution master (tens of megabytes) is streamed to the destination
/// and never buffered into memory.
/// </summary>
public class WallStitchClientTests
{
    [Fact]
    public async Task DownloadArtifact_StreamsToTheDestination_WithoutBuffering()
    {
        var payload = Encoding.ASCII.GetBytes("master-bytes");
        var content = new StreamOnlyContent(payload);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var client = CreateClient(handler);

        await using var destination = new StreamingDestination();
        await client.DownloadArtifactAsync("job-1", "ortho.png", destination);

        Assert.Equal(payload, destination.ToArray());

        // StreamOnlyContent throws when it is serialised into a MemoryStream, which is exactly what
        // ReadAsByteArrayAsync/LoadIntoBufferAsync would do. Reaching here proves the body was
        // copied straight into the caller's stream.
        Assert.False(content.WasBuffered);
        Assert.Equal("jobs/job-1/artifacts/ortho.png", handler.LastPath);
    }

    [Fact]
    public async Task DownloadArtifact_WouldFail_IfTheBodyWereBuffered()
    {
        // Guards the guard: the detector really does trip when someone buffers the response.
        var content = new StreamOnlyContent(Encoding.ASCII.GetBytes("master-bytes"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task CreateJob_PostsMultipartWithPhotosOptionsAndOldPhoto()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Accepted, """{"jobId":"abc","status":"queued"}"""));
        var client = CreateClient(handler);

        var result = await client.CreateJobAsync(
            [new StitchPhotoUpload("1.jpeg", "image/jpeg", [1]), new StitchPhotoUpload("2.jpeg", "image/jpeg", [2])],
            new StitchJobOptions(45.0, "angled", true, null, null, [new StitchHoldInput(Guid.NewGuid(), 0.5, 0.3, 0.01, null, "pink", 0, 3)]),
            new StitchPhotoUpload("old.jpg", "image/jpeg", [9]));

        Assert.Equal("abc", result.JobId);
        Assert.Equal("queued", result.Status);
        Assert.Equal("jobs", handler.LastPath);
        Assert.Contains("name=photos", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=oldPhoto", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"wallAngleDegrees\":45", handler.LastBody);
        Assert.Contains("\"defaultProjection\":\"angled\"", handler.LastBody);
        Assert.Contains("\"boulderLinkCount\":3", handler.LastBody);
    }

    [Fact]
    public async Task GetJob_ParsesTheContractShape()
    {
        const string body = """
        {"jobId":"abc","status":"succeeded","progress":1.0,"stage":"done","error":null,
         "result":{"ortho":{"artifact":"ortho.png","width":7648,"height":4864},
                   "angled":{"artifact":"angled.png","width":7648,"height":3439},
                   "displayOrtho":"display-ortho.jpg","displayAngled":"display-angled.jpg",
                   "wallAngleDegrees":45.0,"verticalScale":0.7071,
                   "diagnostics":{"imagesUsed":["1.jpeg"],"imagesRejected":[{"name":"5.jpeg","reason":"blurry"}],
                                  "seamAngleRmsDeg":0.062,"bowMedianPx":1.13,"coverageWarnings":[]},
                   "holds":[{"id":"8f6b1f6c-1f2a-4c9d-9a11-2f0a1b2c3d4e","x":0.5,"y":0.31,"radius":0.011,
                             "shapePoints":[{"dx":0.01,"dy":-0.02}],"classification":"matched","confidence":0.87}]}}
        """;
        var client = CreateClient(new StubHandler(_ => Json(HttpStatusCode.OK, body)));

        var state = await client.GetJobAsync("abc");

        Assert.Equal("succeeded", state.Status);
        Assert.Equal(1.0, state.Progress);
        Assert.NotNull(state.Result);
        Assert.Equal("ortho.png", state.Result!.Ortho.Artifact);
        Assert.Equal(3439, state.Result.Angled.Height);
        Assert.Equal(0.7071, state.Result.VerticalScale, 4);
        Assert.Equal("blurry", state.Result.Diagnostics!.ImagesRejected![0].Reason);
        Assert.Equal("matched", state.Result.Holds![0].Classification);
        Assert.Equal(-0.02, state.Result.Holds[0].ShapePoints![0].Dy, 6);
    }

    [Fact]
    public async Task TransientFailures_AreRetried_UpToTheConfiguredBound()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : Json(HttpStatusCode.OK, """{"jobId":"abc","status":"running","progress":0.5,"stage":"blending"}""");
        });

        var state = await CreateClient(handler).GetJobAsync("abc");

        Assert.Equal(3, attempts);
        Assert.Equal("running", state.Status);
    }

    [Fact]
    public async Task TransientFailures_EventuallyGiveUp()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(handler).GetJobAsync("abc"));

        // MaxRetries = 2 in the test settings, so three attempts in total.
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task DeleteJob_TreatsAMissingJobAsDone()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await CreateClient(handler).DeleteJobAsync("gone");
        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
    }

    [Fact]
    public async Task AnUnconfiguredClient_RefusesToCall()
    {
        var settings = new BlocwerkSettings();
        var client = new WallStitchClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), settings, NullLogger<WallStitchClient>.Instance);

        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetJobAsync("abc"));
    }

    private static WallStitchClient CreateClient(StubHandler handler)
    {
        var settings = new BlocwerkSettings();
        settings.WallStitch.BaseUrl = "http://stitch.test/";
        settings.WallStitch.MaxRetries = 2;
        settings.WallStitch.RetryBaseDelay = TimeSpan.FromMilliseconds(1);

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stitch.test/") };
        return new WallStitchClient(http, settings, NullLogger<WallStitchClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
