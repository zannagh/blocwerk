// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>A GitHub that accepts the connection and never answers, for the release-notes timeout.</summary>
internal sealed class HungGitHub : HttpMessageHandler, IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}
