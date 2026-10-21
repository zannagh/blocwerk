// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>One running request to the stream route: its body as it is written, and the request to cancel or await.</summary>
internal sealed class FeedStreamRun : IAsyncDisposable
{
    private readonly CancellationTokenSource aborted = new();

    private FeedStreamRun(FeedRouteFixture fixture, string? query, string? lastEventId)
    {
        Request = fixture.GetAsync(
            FeedEndpoints.StreamRoute,
            fixture.WallId,
            query,
            Body,
            http =>
            {
                if (lastEventId is not null)
                {
                    http.Request.Headers[FeedStreamEndpoint.LastEventIdHeader] = lastEventId;
                }
            },
            aborted.Token);
    }

    public SseCapture Body { get; } = new();

    public Task<DefaultHttpContext> Request { get; }

    public static FeedStreamRun Open(FeedRouteFixture fixture, string? query = null, string? lastEventId = null) =>
        new(fixture, query, lastEventId);

    /// <summary>Waits for the server to end the stream on its own.</summary>
    public async Task<DefaultHttpContext> EndedAsync(double seconds = 10) => await Request.WaitAsync(TimeSpan.FromSeconds(seconds));

    public async ValueTask DisposeAsync()
    {
        await aborted.CancelAsync();
        await Request.WaitAsync(TimeSpan.FromSeconds(10));
        aborted.Dispose();
    }
}
