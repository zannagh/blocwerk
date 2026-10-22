// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;
using Microsoft.AspNetCore.Http.Features;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// Writes the <c>text/event-stream</c> framing (<see cref="FeedStreamEvents"/>). Every event's data is one line of JSON
/// (the serializer escapes line breaks), and every event but a goodbye carries an <c>id:</c> that is a feed cursor.
/// </summary>
internal sealed class FeedStreamWriter(HttpResponse response)
{
    public const string ContentType = "text/event-stream";

    /// <summary>Sends the headers and the reconnect delay, and turns off response buffering.</summary>
    public async Task StartAsync(TimeSpan retry, CancellationToken ct)
    {
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-store, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var ms = ((long)retry.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        await WriteAsync("retry: " + ms + "\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public Task EventAsync(FeedEnvelope envelope, CancellationToken ct) =>
        WriteAsync(Frame(envelope.Cursor, null, JsonSerializer.Serialize(envelope, FeedJson.Options)), ct);

    public Task ResyncAsync(FeedCursor next, CancellationToken ct)
    {
        var body = new FeedChanges([], next.ToString(), Resync: true, More: false);
        return WriteAsync(Frame(body.Next, FeedStreamEvents.Resync, JsonSerializer.Serialize(body, FeedJson.Options)), ct);
    }

    public async Task ByeAsync(string reason, CancellationToken ct)
    {
        await WriteAsync(Frame(null, FeedStreamEvents.Bye, JsonSerializer.Serialize(new FeedStreamBye(reason), FeedJson.Options)), ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>A comment line: keeps the connection alive and is ignored by <c>EventSource</c>.</summary>
    public async Task PingAsync(CancellationToken ct)
    {
        await WriteAsync(": ping\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public Task FlushAsync(CancellationToken ct) => response.Body.FlushAsync(ct);

    private static string Frame(string? id, string? name, string json)
    {
        var frame = new StringBuilder();
        if (name is not null)
        {
            frame.Append("event: ").Append(name).Append('\n');
        }

        if (id is not null)
        {
            frame.Append("id: ").Append(id).Append('\n');
        }

        return frame.Append("data: ").Append(json).Append("\n\n").ToString();
    }

    private Task WriteAsync(string text, CancellationToken ct) => response.WriteAsync(text, Encoding.UTF8, ct);
}
