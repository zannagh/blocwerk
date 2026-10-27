// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.WhatsNew;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

public class ReleaseNotesRegistrationTests
{
    [Fact]
    public void The_github_client_is_anonymous_bounded_and_names_itself()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Blocwerk:Releases:Timeout"] = "00:00:03" })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddReleaseNotes();
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubReleaseNotesService.HttpClientName);

        Assert.StartsWith("Blocwerk/", client.DefaultRequestHeaders.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal(TimeSpan.FromSeconds(3), client.Timeout);
        Assert.IsType<GitHubReleaseNotesService>(provider.GetRequiredService<IReleaseNotesService>());
    }
}
