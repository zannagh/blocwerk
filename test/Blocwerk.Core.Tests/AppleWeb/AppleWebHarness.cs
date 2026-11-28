// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Authentication.Apple;
using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.Services;
using Blocwerk.Web.DeviceLinks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>
/// The web Sign in with Apple pieces wired to an isolated database and a stubbed Apple: the real cookie, ticket store,
/// token client, shared validator and link service, with Apple's token endpoint answering <see cref="NextIdToken"/>.
/// </summary>
internal sealed class AppleWebHarness : IDisposable
{
    public const string RedirectUri = "https://blocwerk.test/oauth-callback/apple";

    private readonly SqliteConnection connection;

    public AppleWebHarness()
    {
        var connectionString = TestDbContextFactory.IsolatedDatabase();
        connection = new SqliteConnection(connectionString);
        connection.Open();
        Db = new TestDbContextFactory(connectionString);
        using (var db = Db.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        var settings = AppleWebTestKeys.Settings(AppleWebTestKeys.NewKey().Pem);
        var secret = new AppleClientSecret(settings);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(AppleTokenClient.HttpClientName).Returns(_ => new HttpClient(new AnswerHandler(this)));
        var tokens = new AppleTokenClient(http, settings, secret, NullLogger<AppleTokenClient>.Instance);
        var validator = new AppleIdentityTokenValidator(Apple, new AppleSignInSettings(), NullLogger<AppleIdentityTokenValidator>.Instance);
        Links = new AppleSignInService(
            Db,
            validator,
            new AppleNonceStore(Db, new DataProtectionAppleNonceCodec(new EphemeralDataProtectionProvider())),
            Substitute.For<IApiKeyService>(),
            NullLogger<AppleSignInService>.Instance);
        Accounts = new AppleWebAccounts(Db, NullLogger<AppleWebAccounts>.Instance);
        Web = new AppleWebSignIn(
            settings,
            secret,
            new AppleOAuthCookie(new EphemeralDataProtectionProvider()),
            new AppleCallbackStore(),
            tokens,
            validator,
            new AppleSignInSettings { WebAudiences = [AppleWebTestKeys.ServicesId] },
            Accounts,
            Links);
    }

    public FakeApple Apple { get; } = new();

    public TestDbContextFactory Db { get; }

    public AppleSignInService Links { get; }

    public AppleWebAccounts Accounts { get; }

    public AppleWebSignIn Web { get; }

    /// <summary>What Apple's token endpoint answers with next; null makes it refuse the code.</summary>
    public string? NextIdToken { get; set; }

    /// <summary>
    /// One whole browser round trip up to the verified <c>sub</c>: the authorize redirect, Apple's form POST carrying
    /// <paramref name="userField"/>, the callback taking the code, and the exchange returning a token Apple signed for
    /// <paramref name="subject"/> and <paramref name="audience"/>.
    /// </summary>
    public async Task<(string? Subject, AppleCallbackTicket Ticket)> RoundTripAsync(
        string subject,
        string? userField = null,
        string audience = AppleWebTestKeys.ServicesId,
        string? email = null)
    {
        var state = Guid.NewGuid().ToString();
        var code = $"code-{Guid.NewGuid():N}";
        var start = new DefaultHttpContext();
        var parameters = Web.BeginAuthorize(start.Response, state);

        var post = new DefaultHttpContext();
        post.Request.Headers.Cookie = CookieHeader(start.Response);
        Assert.True(Web.AcceptCallback(post.Request, post.Response, state, code, userField));

        NextIdToken = Apple.IdToken(subject, parameters["nonce"], audience, email);
        Assert.True(Web.TryTakeTicket(code, out var ticket));
        var callback = new DefaultHttpContext();
        callback.Request.Headers.Cookie = CookieHeader(start.Response);
        Assert.True(Web.IsBoundToThisBrowser(ticket, state, callback.Request, callback.Response));
        return (await Web.VerifyAsync(code, ticket, RedirectUri), ticket);
    }

    /// <summary>The request Cookie header that sends back what <paramref name="response"/> set.</summary>
    public static string CookieHeader(HttpResponse response) =>
        response.Headers.SetCookie.ToString().Split(';')[0];

    public void Dispose() => connection.Dispose();

    private sealed class AnswerHandler(AppleWebHarness harness) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var answer = harness.NextIdToken is { } token
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"id_token":"{{token}}"}""") }
                : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
            return Task.FromResult(answer);
        }
    }
}
