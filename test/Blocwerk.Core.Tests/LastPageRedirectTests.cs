using Blocwerk.Web;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The homepage redirect follows a client-recorded cookie, so the validator that decides
/// which stored path is safe has to reject every shape that could bounce an authenticated
/// user to an external origin or back into a one-off auth/share flow.
/// </summary>
public class LastPageRedirectTests
{
    [Theory]
    [InlineData(@"/\evil.com")] // backslash browsers normalise to "//evil.com"
    [InlineData("//evil.com")] // protocol-relative
    [InlineData("http://evil.com")] // absolute with scheme
    [InlineData("/")] // would loop back to the redirect origin
    [InlineData("/account/login")] // auth/account flow
    [InlineData("/join/abc")] // one-off invite link
    [InlineData("/walls/1/boulders/2/shared/tok")] // one-off share-token link
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSafeTarget_RejectsUnsafeOrFlowPaths(string? raw)
    {
        Assert.False(LastPageRedirect.IsSafeTarget(raw));
    }

    [Theory]
    [InlineData("/walls/1/boulders/2")]
    [InlineData("/walls")]
    [InlineData("/walls/1/boulders/2?redirect=http://x")] // a "://" in the query must not fool the check
    public void IsSafeTarget_AcceptsNormalLocalPaths(string raw)
    {
        Assert.True(LastPageRedirect.IsSafeTarget(raw));
    }

    [Fact]
    public void Resolve_ReturnsFallbackForUnsafeTarget()
    {
        Assert.Equal("/walls", LastPageRedirect.Resolve(@"/\evil.com", "/walls"));
    }

    [Fact]
    public void Resolve_ReturnsStoredTargetWhenSafe()
    {
        Assert.Equal("/walls/1", LastPageRedirect.Resolve("/walls/1", "/walls"));
    }
}
