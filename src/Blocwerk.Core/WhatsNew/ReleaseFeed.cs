// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.WhatsNew;

/// <summary>What <see cref="IReleaseNotesService"/> answers: the releases, newest first, or why there are none.</summary>
/// <param name="Releases">Newest first. Empty when GitHub could not be read and nothing was cached.</param>
/// <param name="Available">False when GitHub could not be read and there is nothing to show.</param>
/// <param name="ReleasesPageUrl">The repository's releases page on GitHub, always set.</param>
/// <param name="HtmlBaseUrl">The repository's GitHub page, for building compare links.</param>
public sealed record ReleaseFeed(
    IReadOnlyList<ReleaseNote> Releases,
    bool Available,
    string ReleasesPageUrl,
    string HtmlBaseUrl);
