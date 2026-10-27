// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.WhatsNew;

namespace Blocwerk.Core.Tests;

public class ReleaseListingBuilderTests
{
    private static readonly ReleaseFeed Feed = new(
        [
            Note("0.1.1-224", true),
            Note("0.1.1-220", true),
            Note("v0.1.0", false),
            Note("0.1.0-90", true),
            Note("v0.0.9", false),
        ],
        true,
        "https://github.com/zannagh/blocwerk/releases",
        "https://github.com/zannagh/blocwerk");

    [Fact]
    public void Stable_releases_by_default_plus_the_running_build_highlighted()
    {
        var listing = ReleaseListingBuilder.Build(Feed, "0.1.1-220", includePrereleases: false);

        Assert.Equal(["0.1.1-220", "v0.1.0", "v0.0.9"], listing.Shown.Select(r => r.Tag));
        Assert.Equal("0.1.1-220", listing.Current?.Tag);
        Assert.Equal(2, listing.HiddenPrereleases);
    }

    [Fact]
    public void The_toggle_shows_every_prerelease()
    {
        var listing = ReleaseListingBuilder.Build(Feed, "v0.1.0", includePrereleases: true);

        Assert.Equal(5, listing.Shown.Count);
        Assert.Equal(0, listing.HiddenPrereleases);
        Assert.Equal("v0.1.0", listing.Current?.Tag);
    }

    [Fact]
    public void A_preview_build_newer_than_the_last_stable_release_says_so_with_a_compare_link()
    {
        var listing = ReleaseListingBuilder.Build(Feed, "0.1.1-224", includePrereleases: false);

        Assert.Equal("v0.1.0", listing.PreviewNewerThan?.Tag);
        Assert.Equal("https://github.com/zannagh/blocwerk/compare/v0.1.0...0.1.1-224", listing.CompareUrl);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("v0.1.0")]
    [InlineData("0.1.0-90")]
    [InlineData("dev")]
    public void A_stable_older_or_unversioned_build_gets_no_preview_notice(string running)
    {
        var listing = ReleaseListingBuilder.Build(Feed, running, includePrereleases: false);

        Assert.Null(listing.PreviewNewerThan);
        Assert.Null(listing.CompareUrl);
    }

    [Fact]
    public void A_preview_build_without_its_own_release_still_compares_against_its_version()
    {
        var listing = ReleaseListingBuilder.Build(Feed, "0.2.0-ci.5", includePrereleases: false);

        Assert.Null(listing.Current);
        Assert.Equal("https://github.com/zannagh/blocwerk/compare/v0.1.0...0.2.0-ci.5", listing.CompareUrl);
        Assert.Equal(["v0.1.0", "v0.0.9"], listing.Shown.Select(r => r.Tag));
    }

    [Theory]
    [InlineData("0.1.1-224", "v0.1.0", 1)]
    [InlineData("0.1.0-90", "v0.1.0", -1)]
    [InlineData("0.1.1-224", "0.1.1-99", 1)]
    [InlineData("v0.1.0", "0.1.0", 0)]
    [InlineData("0.2.0-ci.5", "0.2.0-ci.12", -1)]
    public void Versions_compare_by_semver(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(ReleaseVersion.Compare(a, b)!.Value));
    }

    private static ReleaseNote Note(string tag, bool prerelease)
    {
        return new ReleaseNote(tag, tag, null, prerelease, $"https://github.com/zannagh/blocwerk/releases/tag/{tag}", string.Empty);
    }
}
