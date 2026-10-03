using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The full-screen photo takeover (StageOverlay) must always offer a visible, touch-sized way out:
/// a kiosk has no keyboard, so Escape is not available and the close pill is the only exit. These
/// are source assertions because this project has no component-test host to render the overlay.
/// </summary>
public class StageOverlayCloseTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// The close button lives inside the takeover itself (the stage that the overlay fills the
    /// screen with), is never conditional, and carries the hook the lock script listens for.
    /// </summary>
    [Fact]
    public void CloseButton_IsAlwaysRenderedInsideTheStage()
    {
        string markup = ReadSource("src/Blocwerk.Web/Components/Shared/StageOverlay.razor");

        int stage = markup.IndexOf("class=\"bw-stage-rot\"", StringComparison.Ordinal);
        int close = markup.IndexOf("class=\"bw-stage-close\" data-bw-stage-close @onclick=\"CloseAsync\"", StringComparison.Ordinal);
        int firstIf = markup.IndexOf("@if", StringComparison.Ordinal);

        Assert.True(stage >= 0, "stage layer missing");
        Assert.True(close > stage, "close button must sit inside the stage");
        Assert.True(firstIf < 0 || firstIf > close, "close button must not be conditional");
        Assert.Contains("bw-stage-close-label\">Close</span>", markup);
    }

    /// <summary>
    /// Touch-sized (at least 48 px, here 56 px), inset from the screen edges so TV overscan and
    /// kiosk-browser corner controls cannot hide it, and stacked above the photo and the tools.
    /// </summary>
    [Fact]
    public void CloseButton_IsTouchSizedInsetAndOnTop()
    {
        string rule = CssRule(ReadSource("src/Blocwerk.Web/wwwroot/css/fullscreen-stage.css"), ".bw-stage-close");

        Assert.Equal(56, Px(rule, "height"));
        Assert.Equal(56, Px(rule, "min-width"));
        Assert.Contains("top: max(24px, env(safe-area-inset-top, 0px))", rule);
        Assert.Contains("right: max(24px, env(safe-area-inset-right, 0px))", rule);
        Assert.True(Px(rule, "z-index") > 3, "close pill must stack above the bottom chrome");
        Assert.DoesNotContain("display: none", rule);
    }

    /// <summary>
    /// Tapping close leaves real browser fullscreen in the tap itself — not only once the circuit
    /// disposes the overlay — and release() stays safe when fullscreen was already left.
    /// </summary>
    [Fact]
    public void CloseTap_ReleasesBrowserFullscreenImmediately()
    {
        string script = ReadSource("src/Blocwerk.Web/wwwroot/js/fullscreen-stage-lock.js");

        Assert.Contains("closest('[data-bw-stage-close]')", script);
        Assert.Contains("if (document.fullscreenElement && document.exitFullscreen)", script);
    }

    /// <summary>
    /// The root marker must not reuse the open button's class (on &lt;html&gt; it pulled in the
    /// button's absolute 38px box and crushed the page), and the overlay is portaled to &lt;body&gt;
    /// so no sticky ancestor can trap it under the top bar, the tab bar or the cookie banner. The
    /// portal is only safe because the overlay is the sole child of its own Blazor host.
    /// </summary>
    [Fact]
    public void OpenTakeover_IsPortaledAboveThePage()
    {
        string script = ReadSource("src/Blocwerk.Web/wwwroot/js/fullscreen-stage.js");
        string css = ReadSource("src/Blocwerk.Web/wwwroot/css/fullscreen-stage.css");
        string markup = ReadSource("src/Blocwerk.Web/Components/Shared/StageOverlay.razor");

        Assert.DoesNotContain("classList.add('bw-stage-open')", script);
        Assert.Contains("classList.add('bw-stage-active')", script);
        Assert.Contains("ctx.home = portal(root);", script);
        Assert.Contains("document.body.appendChild(root);", script);
        Assert.Contains("unportal(ctx);", script);
        Assert.DoesNotContain("html.bw-stage-open", css);
        Assert.Contains(
            "<div class=\"bw-stage-host\">\n<div class=\"bw-stage-overlay\"",
            markup.Replace("\r\n", "\n"));
    }

    private static string CssRule(string css, string selector)
    {
        Match match = Regex.Match(css, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"rule {selector} missing");
        return match.Groups["body"].Value;
    }

    private static int Px(string rule, string property)
    {
        Match match = Regex.Match(rule, @"(?m)^\s*" + Regex.Escape(property) + @":\s*(?<v>\d+)");
        Assert.True(match.Success, $"{property} missing");
        return int.Parse(match.Groups["v"].Value);
    }

    private static string ReadSource(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepoRoot, relativePath));
    }

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        DirectoryInfo? dir = new FileInfo(thisFile).Directory;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
