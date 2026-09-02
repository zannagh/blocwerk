using System.Runtime.CompilerServices;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Home" is a fixed destination: the home wall's overview, every time, whatever the user was
/// looking at. Two things used to break that — the tab pointed at /home, which resumed the
/// blocwerk-last-page cookie (so it could land on the boulder list), and /home itself resolved on
/// both the prerender and the interactive pass, which could pick different targets.
///
/// These are source assertions because the behaviour lives in markup and in a redirect this project
/// has no component-test host to render.
/// </summary>
public class HomeNavigationTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// The Home tab links straight at the wall, so there is no server-side decision about where
    /// home is and no query string to open the carousel anywhere but the overview.
    /// </summary>
    [Fact]
    public void HomeTab_LinksDirectlyToTheHomeWallOverview()
    {
        string markup = ReadSource("src/Blocwerk.Web/Components/Layout/MainLayout.razor");

        Assert.Contains("<a href=\"@HomeWallHref\" class=\"nav-item\"", markup);
        Assert.Contains("_homeWallId is { } homeWallId ? $\"/walls/{homeWallId}\" : \"/walls\"", markup);
        Assert.DoesNotContain("href=\"/home\"", markup);
    }

    /// <summary>
    /// A tap while already on the home wall's URL changes nothing the carousel can react to, so the
    /// layout hands that case to bwNav — keeping the href so a real navigation still happens
    /// everywhere else, and for middle-click and no-JS loads.
    /// </summary>
    [Fact]
    public void HomeTab_HandsTheSameUrlCaseToTheNavigationScript()
    {
        string markup = ReadSource("src/Blocwerk.Web/Components/Layout/MainLayout.razor");

        Assert.Contains("onclick=\"return bwNav.goHomeWall(event, this.href)\"", markup);
    }

    /// <summary>
    /// The kiosk tab bar has its own Home tab pointing at the tablet's registered wall. It is the
    /// tab most likely to be tapped after a swipe — a kiosk user never leaves the wall — so it
    /// needs the same handler; without it the tap is simply dead.
    /// </summary>
    [Fact]
    public void KioskHomeTab_HandsTheSameUrlCaseToTheNavigationScript()
    {
        string markup = ReadSource("src/Blocwerk.Web/Components/Layout/MainLayout.razor");

        Assert.Contains(
            "<a href=\"@KioskWallHref\" class=\"nav-item\"\n"
            + "               onclick=\"return bwNav.goHomeWall(event, this.href)\">",
            markup.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The script half of the same-URL case: it only intervenes on the exact current URL, moves the
    /// wall carousel to the overview, and otherwise lets the navigation happen. The scrolling itself
    /// is browser behaviour this project has no harness to assert — this pins the wiring only.
    /// </summary>
    [Fact]
    public void NavigationScript_MovesTheCarouselOnlyWhenTheUrlAlreadyMatches()
    {
        string script = ReadSource("src/Blocwerk.Web/wwwroot/js/nav.js");

        Assert.Contains("goHomeWall: function (event, href)", script);
        Assert.Contains("target.pathname !== window.location.pathname || target.search !== window.location.search", script);
        Assert.Contains("window.wallCarousel.scrollToPage(el, WALL_OVERVIEW_PAGE, true)", script);
        Assert.Contains("const WALL_OVERVIEW_PAGE = 2;", script);
    }

    /// <summary>
    /// /home stays reachable for a bookmark or a restored tab, but must resolve to exactly one
    /// target — no cookie resume, so the prerender and interactive passes cannot disagree.
    /// </summary>
    [Fact]
    public void HomeRedirect_ResolvesToTheWallOverviewWithoutResumingTheLastPage()
    {
        string markup = ReadSource("src/Blocwerk.Web/Components/Pages/HomeWall.razor");

        Assert.Contains("Navigation.NavigateTo($\"/walls/{homeWallId}\", replace: true);", markup);
        Assert.DoesNotContain("blocwerk-last-page", markup);
        Assert.DoesNotContain("?view=", markup);
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
