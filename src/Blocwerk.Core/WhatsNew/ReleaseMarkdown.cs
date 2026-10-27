// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Blocwerk.Core.WhatsNew;

/// <summary>
/// Renders release notes from GitHub to HTML that is safe to put on the page as markup.
/// </summary>
/// <remarks>
/// The notes are written by whoever can publish a release, and the page renders them as raw markup,
/// so this is the whole of the defence: raw HTML in the Markdown is disabled (Markdig writes it out
/// escaped, as text), a link or image whose URL is not http(s) or mailto loses the URL, and every
/// link opens in a new tab with <c>rel="noopener noreferrer"</c>. Markdig's generic-attributes
/// extension is deliberately NOT enabled: <c>{onclick=...}</c> after a link would set an attribute.
/// </remarks>
public static class ReleaseMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .Build();

    /// <summary>The notes as safe HTML; empty for no notes.</summary>
    public static string ToSafeHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            link.Url = SafeUrl(link.Url);
            if (!link.IsImage)
            {
                MarkExternal(link);
            }
        }

        foreach (var autolink in document.Descendants<AutolinkInline>())
        {
            autolink.Url = SafeUrl(autolink.Url) ?? string.Empty;
            MarkExternal(autolink);
        }

        return document.ToHtml(Pipeline);
    }

    /// <summary>The URL when it is absolute http(s) or mailto; otherwise null, which drops it.</summary>
    internal static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" or "mailto" ? url.Trim() : null;
    }

    private static void MarkExternal(MarkdownObject link)
    {
        var attributes = link.GetAttributes();
        attributes.AddPropertyIfNotExist("target", "_blank");
        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
    }
}
