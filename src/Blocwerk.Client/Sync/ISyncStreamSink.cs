// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>Hears one wall's feed stream (<c>/feed/stream</c>), in the order the browser received it.</summary>
public interface ISyncStreamSink
{
    /// <summary>
    /// One stream message: <c>open</c> (the stream's 200 arrived), <c>message</c> (a feed envelope), <c>resync</c> (a
    /// <c>FeedChanges</c>), <c>bye</c>
    /// (a <c>FeedStreamBye</c>; the stream is closed), or <c>error</c> (the stream failed or was refused, and is closed:
    /// an <c>EventSource</c> hides the status, so the loop probes <c>/changes</c> to find out).
    /// </summary>
    /// <param name="type">The message type.</param>
    /// <param name="data">Its JSON, or null.</param>
    /// <returns>A task that completes once the message was taken in.</returns>
    Task OnMessageAsync(string type, string? data);
}
