// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Authentication.Apple;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using NSubstitute;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>The code exchange at Apple's token endpoint, against a stub.</summary>
public class AppleTokenClientTests
{
    [Fact]
    public async Task TheExchange_PostsTheCode_WithTheServicesId_AndAClientSecretJwt_AndReturnsTheIdToken()
    {
        var stub = StubAppleHandler.WithIdToken("the.id.token");
        var client = Client(stub, out _);

        var idToken = await client.ExchangeAsync("code-1", "https://blocwerk.test/oauth-callback/apple");

        Assert.Equal("the.id.token", idToken);
        Assert.Equal("https://apple.test/auth/token", stub.Urls.Single()!.ToString());
        var form = stub.Forms.Single();
        Assert.Equal(AppleWebTestKeys.ServicesId, form["client_id"]);
        Assert.Equal("code-1", form["code"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("https://blocwerk.test/oauth-callback/apple", form["redirect_uri"]);
        Assert.Equal(AppleWebTestKeys.ServicesId, new JsonWebToken(form["client_secret"]).Subject);
    }

    [Fact]
    public async Task ARefusedOrMalformedAnswer_GivesNoToken()
    {
        Assert.Null(await Client(new StubAppleHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""), out _)
            .ExchangeAsync("code", "https://blocwerk.test/oauth-callback/apple"));
        Assert.Null(await Client(new StubAppleHandler(HttpStatusCode.OK, "not json"), out _)
            .ExchangeAsync("code", "https://blocwerk.test/oauth-callback/apple"));
        Assert.Null(await Client(new StubAppleHandler(HttpStatusCode.OK, "{}"), out _)
            .ExchangeAsync("code", "https://blocwerk.test/oauth-callback/apple"));
    }

    [Fact]
    public async Task WithoutAKey_NothingIsSentToApple()
    {
        var stub = StubAppleHandler.WithIdToken("x");
        var settings = AppleWebTestKeys.Settings(string.Empty);
        var client = new AppleTokenClient(Factory(stub), settings, new AppleClientSecret(settings), NullLogger<AppleTokenClient>.Instance);

        Assert.Null(await client.ExchangeAsync("code", "https://blocwerk.test/oauth-callback/apple"));
        Assert.Empty(stub.Forms);
    }

    internal static AppleTokenClient Client(StubAppleHandler stub, out AppleWebSettings settings)
    {
        settings = AppleWebTestKeys.Settings(AppleWebTestKeys.NewKey().Pem);
        return new AppleTokenClient(Factory(stub), settings, new AppleClientSecret(settings), NullLogger<AppleTokenClient>.Instance);
    }

    private static IHttpClientFactory Factory(StubAppleHandler stub)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AppleTokenClient.HttpClientName).Returns(_ => new HttpClient(stub, disposeHandler: false));
        return factory;
    }
}
