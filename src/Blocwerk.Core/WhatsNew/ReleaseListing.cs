// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.WhatsNew;

/// <summary>What /whats-new shows: the release list filtered for the prerelease toggle, and the running build in it.</summary>
/// <param name="Shown">The releases to list, newest first.</param>
/// <param name="Current">The release the running build is, when it is one of the fetched releases.</param>
/// <param name="PreviewNewerThan">The latest stable release, when the running build is a newer preview build.</param>
/// <param name="CompareUrl">GitHub's compare view from that stable release to the running build.</param>
/// <param name="HiddenPrereleases">How many prereleases the toggle is hiding.</param>
public sealed record ReleaseListing(
    IReadOnlyList<ReleaseNote> Shown,
    ReleaseNote? Current,
    ReleaseNote? PreviewNewerThan,
    string? CompareUrl,
    int HiddenPrereleases);
