// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Blocwerk.Core.WhatsNew;

/// <summary>Registers the release-notes feed behind /whats-new.</summary>
public static class ReleaseNotesServices
{
    /// <summary>The options, the named GitHub client (anonymous, bounded, with a User-Agent) and the cached feed.</summary>
    public static IServiceCollection AddReleaseNotes(this IServiceCollection services)
    {
        services.AddOptions<ReleaseNotesOptions>().BindConfiguration(ReleaseNotesOptions.SectionName);
        services.AddHttpClient(GitHubReleaseNotesService.HttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<ReleaseNotesOptions>>().Value;
            client.Timeout = options.Timeout;

            // GitHub refuses API requests without a User-Agent.
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                $"Blocwerk/{ReleaseVersion.Normalize(AppVersion.Current)} (+https://github.com/{ReleaseNotesOptions.DefaultRepository})");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        });
        services.AddSingleton<IReleaseNotesService, GitHubReleaseNotesService>();
        return services;
    }
}
