// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.WhatsNew;

/// <summary>Where /whats-new reads Blocwerk's release notes from. Section <c>Blocwerk:Releases</c>.</summary>
public sealed class ReleaseNotesOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Blocwerk:Releases";

    /// <summary>The repository used when <see cref="Repository"/> is missing or malformed.</summary>
    public const string DefaultRepository = "zannagh/blocwerk";

    /// <summary>The public GitHub repository, as <c>owner/name</c>. Read anonymously: no token, ever.</summary>
    public string Repository { get; set; } = DefaultRepository;

    /// <summary>The REST API root. Only the browser tests point this anywhere else (a local stub).</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>The site the "all releases" and compare links point at.</summary>
    public string HtmlBaseUrl { get; set; } = "https://github.com";

    /// <summary>
    /// How long a fetched list is served before GitHub is asked again. Unauthenticated callers get 60
    /// requests an hour per IP, and a revalidation that answers 304 is free, so an hour is plenty.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long to wait before asking again after GitHub failed, so an outage is not hammered.</summary>
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The bound on one whole fetch, every page included. The page never waits longer.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The most pages of 100 to read. Paging only goes on while no stable release has been seen, since
    /// every deploy publishes a prerelease and the last stable one can sit far down the list.
    /// </summary>
    public int MaxPages { get; set; } = 3;
}
