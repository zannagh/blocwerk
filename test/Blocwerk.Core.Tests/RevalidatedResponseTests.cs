// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Release fix W5: identity-dependent bytes are revalidated on every use (<c>private, no-cache</c> plus an ETag), never
/// kept by a <c>max-age</c> a revoked viewer or the next identity on a shared device could still read from.
/// </summary>
public partial class RevalidatedResponseTests
{
    [Fact]
    public async Task Bytes_AreNoCache_UnderAContentETag_AndAHolderGetsA304()
    {
        byte[] bytes = [1, 2, 3];
        var first = new DefaultHttpContext();
        RevalidatedResponse.Bytes(first, bytes, "image/jpeg");
        var etag = first.Response.Headers.ETag.ToString();

        var again = new DefaultHttpContext();
        again.Request.Headers.IfNoneMatch = etag;
        var result = RevalidatedResponse.Bytes(again, bytes, "image/jpeg");
        await result.ExecuteAsync(again.WithServices());

        Assert.Equal("private, no-cache", first.Response.Headers.CacheControl.ToString());
        Assert.StartsWith("\"", etag, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status304NotModified, again.Response.StatusCode);
    }

    /// <summary>No endpoint keeps identity-dependent bytes by max-age; only the immutable hashed policy has one.</summary>
    [Fact]
    public void NoEndpoint_SendsAMaxAge_ButTheImmutablePolicy()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "src", "Blocwerk.Web", "Endpoints"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select(l => (File: Path.GetFileName(f), Line: l.Trim())))
            .Where(x => MaxAge().IsMatch(x.Line) && !x.Line.StartsWith("//", StringComparison.Ordinal) && !x.Line.Contains("31536000", StringComparison.Ordinal))
            .Select(x => $"{x.File}: {x.Line}")
            .ToList();

        Assert.Empty(offenders);
    }

    [GeneratedRegex("\"[^\"]*max-age=")]
    private static partial Regex MaxAge();

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new FileInfo(thisFile).Directory;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
