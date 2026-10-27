// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.WhatsNew;

/// <summary>Turns the fetched feed into what /whats-new lists for one running version and toggle state.</summary>
public static class ReleaseListingBuilder
{
    /// <summary>
    /// Stable releases only unless <paramref name="includePrereleases"/>; the running build's own
    /// release is listed either way, so it can be highlighted even when it is a prerelease.
    /// </summary>
    public static ReleaseListing Build(ReleaseFeed feed, string runningVersion, bool includePrereleases)
    {
        var current = feed.Releases.FirstOrDefault(r => ReleaseVersion.SameVersion(r.Tag, runningVersion));
        var shown = feed.Releases
            .Where(r => includePrereleases || !r.IsPrerelease || ReferenceEquals(r, current))
            .ToList();
        var latestStable = feed.Releases.FirstOrDefault(r => !r.IsPrerelease);
        var isNewerPreview = latestStable is not null
            && ReleaseVersion.IsPrerelease(runningVersion)
            && ReleaseVersion.Compare(runningVersion, latestStable.Tag) > 0;
        var previewNewerThan = isNewerPreview ? latestStable : null;
        var compareUrl = previewNewerThan is null
            ? null
            : $"{feed.HtmlBaseUrl}/compare/{Uri.EscapeDataString(previewNewerThan.Tag)}..."
              + Uri.EscapeDataString(current?.Tag ?? ReleaseVersion.Normalize(runningVersion));
        return new ReleaseListing(shown, current, previewNewerThan, compareUrl, feed.Releases.Count - shown.Count);
    }
}
