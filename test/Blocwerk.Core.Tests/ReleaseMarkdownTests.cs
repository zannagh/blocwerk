// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.WhatsNew;

namespace Blocwerk.Core.Tests;

public class ReleaseMarkdownTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Fixes things.\n\n<script>alert(1)</script>\n")]
    [InlineData("inline <script>alert(1)</script> here")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    public void Raw_html_in_the_notes_never_renders_as_markup(string markdown)
    {
        var html = ReleaseMarkdown.ToSafeHtml(markdown);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JavaScript:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("![x](javascript:alert(1))")]
    [InlineData("[click][ref]\n\n[ref]: javascript:alert(1)")]
    [InlineData("<javascript:alert(1)>")]
    public void Script_urls_are_dropped(string markdown)
    {
        var html = ReleaseMarkdown.ToSafeHtml(markdown);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generic_attributes_are_not_an_injection_route()
    {
        var html = ReleaseMarkdown.ToSafeHtml("[x](https://example.com){onclick=alert(1)}");

        Assert.DoesNotContain("onclick=\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Links_and_bare_urls_open_in_a_new_tab_with_noopener()
    {
        var html = ReleaseMarkdown.ToSafeHtml(
            "* fix: things by @zannagh in [#219](https://github.com/zannagh/blocwerk/pull/219)\n\n"
            + "**Full Changelog**: https://github.com/zannagh/blocwerk/compare/a...b");

        Assert.Contains("<a href=\"https://github.com/zannagh/blocwerk/pull/219\"", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://github.com/zannagh/blocwerk/compare/a...b\"", html, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(html, "rel=\"noopener noreferrer\""));
        Assert.Equal(2, CountOf(html, "target=\"_blank\""));
        Assert.Contains("<strong>Full Changelog</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<li>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_notes_render_as_nothing(string? markdown)
    {
        Assert.Equal(string.Empty, ReleaseMarkdown.ToSafeHtml(markdown));
    }

    private static int CountOf(string text, string part)
    {
        return (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;
    }
}
