using System.Net.Http;

namespace Blocwerk.Core.Tests;

/// <summary>Records the last request and replies with whatever the factory returns.</summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        this.responder = responder;
    }

    public string? LastPath { get; private set; }

    public HttpMethod? LastMethod { get; private set; }

    public string LastBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastPath = request.RequestUri?.AbsolutePath.TrimStart('/');
        LastMethod = request.Method;
        LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        return responder(request);
    }
}

/// <summary>
/// The destination a streamed artifact download must land in. Used as a sentinel by
/// <see cref="StreamOnlyContent"/>: only a direct <c>CopyToAsync(destination)</c> hands this exact
/// stream to the content, so any buffering path is detectable.
/// </summary>
public sealed class StreamingDestination : MemoryStream
{
}

/// <summary>
/// Response content that refuses to be buffered. Anything other than a direct copy into a
/// <see cref="StreamingDestination"/> — <c>ReadAsByteArrayAsync</c>, <c>LoadIntoBufferAsync</c>,
/// or the default <c>ResponseContentRead</c> completion option — serialises into an internal
/// buffer stream instead, which this content rejects.
/// </summary>
public sealed class StreamOnlyContent : HttpContent
{
    private readonly byte[] payload;

    public StreamOnlyContent(byte[] payload)
    {
        this.payload = payload;
    }

    public bool WasBuffered { get; private set; }

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        if (stream is not StreamingDestination)
        {
            WasBuffered = true;
            throw new InvalidOperationException("The stitch artifact body was buffered into memory instead of streamed.");
        }

        // Written in chunks so a partially-consumed download is observable.
        for (var offset = 0; offset < payload.Length; offset += 4)
        {
            await stream.WriteAsync(payload.AsMemory(offset, Math.Min(4, payload.Length - offset)));
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
