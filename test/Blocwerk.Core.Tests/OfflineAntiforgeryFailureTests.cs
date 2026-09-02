using System.Runtime.CompilerServices;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline queue is allowed to delete an entry only when the server has genuinely refused the
/// action. A missing or stale antiforgery token is NOT that: it is a property of the page and the
/// session, not of the climb somebody logged, and treating it as permanent destroys real user data.
///
/// These are source assertions. The behaviour is browser JavaScript driven by fetch failures, and
/// this repository has no JavaScript test harness, so what is pinned here is the wiring — that the
/// tokenless-POST fallback stays deleted and that a 400 maps to a keep, not a drop. The runtime
/// paths themselves are only reachable in a browser.
/// </summary>
public class OfflineAntiforgeryFailureTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// A failed token mint must reject rather than resolve to null. Resolving to null sent the POST
    /// with no header at all, which is a guaranteed 400 — one transient 502 on the token GET was
    /// enough to turn a queued attempt into a deleted one.
    /// </summary>
    [Fact]
    public void TokenMint_RejectsInsteadOfResolvingToNull()
    {
        string script = ReadSource("src/Blocwerk.Web/wwwroot/js/offline-transport.js");

        Assert.DoesNotContain(".catch(() => null)", script);
        Assert.Contains("throw new Error('No security token in response.')", script);
        Assert.Contains("Could not obtain a security token", script);
    }

    /// <summary>
    /// The header is attached from a token the transport actually holds; there is no path that
    /// posts without one. Retrying later is the fix, never letting the server accept a bare post.
    /// </summary>
    [Fact]
    public void Post_NeverFallsBackToATokenlessRequest()
    {
        string script = ReadSource("src/Blocwerk.Web/wwwroot/js/offline-transport.js");

        Assert.DoesNotContain("rawPost(url, body, null)", script);
        Assert.Contains("return fetchAntiforgeryToken().then(token => rawPost(url, body, token));", script);
    }

    /// <summary>
    /// 400 is the status a rejected token shares with a bad payload, so it is deferred, not
    /// dropped; only the other 4xx are permanent. The 7-day expiry stays the backstop.
    /// </summary>
    [Fact]
    public void Classification_TreatsA400AsRetryableAndLeavesTheOther4xxPermanent()
    {
        string script = ReadSource("src/Blocwerk.Web/wwwroot/js/offline-transport.js");

        Assert.Contains("if (response.status === 400) {\n                return 'defer';",
            script.Replace("\r\n", "\n"));
        Assert.Contains("if (response.status > 400 && response.status < 500) {",
            script.Replace("\r\n", "\n"));
        Assert.DoesNotContain("if (response.status >= 400 && response.status < 500) {", script);
    }

    /// <summary>
    /// A deferred entry can outlive its attempt by up to seven days, so it must not stop the run —
    /// otherwise one unattributable 400 wedges every later action behind it. Only a pause (401) and
    /// a transient retry stop the flush.
    /// </summary>
    [Fact]
    public void Flush_IsNotStoppedByADeferredEntry()
    {
        string script = ReadSource("src/Blocwerk.Web/wwwroot/js/offline-queue.js");

        Assert.Contains("return outcome === 'pause' || outcome === 'retry';", script);
        Assert.Contains("Date.now() - entry.createdAt > MAX_AGE_MS", script);
        Assert.Contains("const MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;", script);
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
