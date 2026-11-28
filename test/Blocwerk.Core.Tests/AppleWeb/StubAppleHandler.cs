// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Text;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>Stands in for Apple's token endpoint: records each request's form and answers with a fixed response.</summary>
internal sealed class StubAppleHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<Dictionary<string, string>> Forms { get; } = [];

    public List<Uri?> Urls { get; } = [];

    /// <summary>An answer carrying <paramref name="idToken"/> as Apple's token endpoint does.</summary>
    public static StubAppleHandler WithIdToken(string idToken) =>
        new(HttpStatusCode.OK, $$"""{"access_token":"a","token_type":"Bearer","expires_in":3600,"refresh_token":"r","id_token":"{{idToken}}"}""");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Urls.Add(request.RequestUri);
        var form = await request.Content!.ReadAsStringAsync(cancellationToken);
        Forms.Add(form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(
            p => Uri.UnescapeDataString(p[0]),
            p => Uri.UnescapeDataString(p[1].Replace('+', ' '))));
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
