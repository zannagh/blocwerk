// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An in-memory host with only the REAL <see cref="ChangeJournalAdminController"/>, the app's personal-key policy and a
/// header-driven stand-in for the API-key scheme (<see cref="HeaderApiKeyAuthHandler"/>), over a substitute service.
/// </summary>
internal sealed class JournalApiHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private JournalApiHost(WebApplication app)
    {
        this.app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<JournalApiHost> StartAsync(IChangeJournalAdminService journal)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(journal);
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ChangeJournalAdminController).Assembly)
            .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyJournalController()));
        builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderApiKeyAuthHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddPolicy(
            BlocwerkPolicies.HumanOrUserApiKey, HumanOrPersonalApiKeyPolicy.Build(new AuthorizationPolicyBuilder())));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return new JournalApiHost(app);
    }

    public Task<HttpResponseMessage> SendAsync(string method, string path, string? who, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        if (who is not null)
        {
            request.Headers.Add(HeaderApiKeyAuthHandler.Header, who);
        }

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
