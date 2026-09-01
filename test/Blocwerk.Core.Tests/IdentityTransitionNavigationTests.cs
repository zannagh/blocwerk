using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Blazor's enhanced navigation keeps the current circuit alive and only patches the DOM. Every
/// scoped service in that circuit survives with it — <c>CurrentUserService</c>'s cached
/// <c>User</c>, <c>CookieAuthenticationStateProvider</c>'s cached <c>AuthenticationState</c>, and
/// <c>SessionState</c>'s active climbing session. So any link that CHANGES WHO IS SIGNED IN has to
/// opt out of enhanced navigation, or the previous person's state bleeds into the next one's pages
/// (a shared laptop, and every kiosk tablet by construction).
///
/// These are source assertions because the markup is what the framework reads: there is no
/// component-test host in this project, and the property under test ("the browser really navigates")
/// lives in the rendered attribute, not in any C# we could call.
/// </summary>
public class IdentityTransitionNavigationTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// The header Logout link must force a real page load so the next visitor gets a fresh circuit.
    /// </summary>
    [Fact]
    public void HeaderLogoutLink_OptsOutOfEnhancedNavigation()
    {
        string markup = ReadSource("src/Blocwerk.Web/Components/Layout/MainLayout.razor");

        string? anchor = FindAnchor(markup, "/account/logout");

        Assert.NotNull(anchor);
        Assert.Contains("data-enhance-nav=\"false\"", anchor);
    }

    /// <summary>
    /// The Login link sits in the same header and is the other half of the identity transition.
    /// </summary>
    [Fact]
    public void HeaderLoginLink_OptsOutOfEnhancedNavigation()
    {
        string markup = ReadSource("src/Blocwerk.Web/Components/Layout/MainLayout.razor");

        string? anchor = FindAnchor(markup, "/account/login");

        Assert.NotNull(anchor);
        Assert.Contains("data-enhance-nav=\"false\"", anchor);
    }

    /// <summary>
    /// Guards the whole class of defect rather than the one link that was found: no component
    /// anywhere may link to the logout endpoint through an enhanced navigation.
    /// </summary>
    [Fact]
    public void NoComponentLinksToLogoutWithEnhancedNavigation()
    {
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot, "src"), "*.razor", SearchOption.AllDirectories))
        {
            string markup = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(markup, "<a\\b[^>]*>", RegexOptions.Singleline))
            {
                if (match.Value.Contains("/account/logout", StringComparison.Ordinal)
                    && !match.Value.Contains("data-enhance-nav=\"false\"", StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetRelativePath(RepoRoot, file));
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The kiosk idle timeout releases the session by navigating to the same logout endpoint from
    /// C#; that path has to force-load for exactly the same reason the link does.
    /// </summary>
    [Fact]
    public void KioskIdleReleaseNavigatesWithForceLoad()
    {
        string source = ReadSource("src/Blocwerk.Web/State/KioskCircuitHandler.cs");

        Assert.Contains("NavigateTo(\"/account/logout\", forceLoad: true)", source);
    }

    /// <summary>
    /// The state that must not outlive a logout is only rebuilt on a new page load if it is scoped
    /// in the first place. A singleton here would survive the full navigation too, and the fix would
    /// be worthless.
    /// </summary>
    [Theory]
    [InlineData("src/Blocwerk.Authentication/AuthenticationServices.cs", "AddScoped<ICurrentUserService, CurrentUserService>()")]
    [InlineData("src/Blocwerk.Authentication/AuthenticationServices.cs", "AddScoped<AuthenticationStateProvider, CookieAuthenticationStateProvider>()")]
    [InlineData("src/Blocwerk.Web/Program.cs", "AddScoped<SessionState>()")]
    public void PerUserStateIsScoped(string relativePath, string registration)
    {
        Assert.Contains(registration, ReadSource(relativePath));
    }

    /// <summary>
    /// Returns the opening tag of the first anchor pointing at <paramref name="href"/>, or null.
    /// </summary>
    private static string? FindAnchor(string markup, string href)
    {
        foreach (Match match in Regex.Matches(markup, "<a\\b[^>]*>", RegexOptions.Singleline))
        {
            if (match.Value.Contains($"href=\"{href}\"", StringComparison.Ordinal))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static string ReadSource(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Walks up from this test file (its compile-time path, so it is independent of the output
    /// directory layout) until the solution file appears.
    /// </summary>
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
