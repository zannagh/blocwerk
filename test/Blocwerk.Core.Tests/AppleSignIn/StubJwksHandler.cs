// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>Serves a JWKS document for Apple's key URL and counts how often it was asked.</summary>
internal sealed class StubJwksHandler : HttpMessageHandler
{
    public StubJwksHandler(string json)
    {
        Json = json;
    }

    public string Json { get; set; }

    public int Fetches { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Fetches++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json) });
    }
}
