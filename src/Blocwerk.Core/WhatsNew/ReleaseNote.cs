// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.WhatsNew;

/// <summary>One published GitHub release, its notes already rendered to safe HTML.</summary>
/// <param name="Tag">The git tag, e.g. <c>v0.1.0</c> or <c>0.1.1-224</c>.</param>
/// <param name="Name">The release title; the tag when the release has none.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="IsPrerelease">True for GitHub prereleases (every deploy between two stable versions).</param>
/// <param name="HtmlUrl">The release's own page on GitHub.</param>
/// <param name="BodyHtml">The notes, rendered by <see cref="ReleaseMarkdown"/>: no raw HTML, only safe links.</param>
public sealed record ReleaseNote(
    string Tag,
    string Name,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    string HtmlUrl,
    string BodyHtml);
