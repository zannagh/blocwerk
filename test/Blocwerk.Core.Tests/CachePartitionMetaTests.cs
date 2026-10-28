// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.RegularExpressions;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The <c>bw-partition</c> meta (design §6.6) and the key behind the labels. Every <c>@page</c> is rendered inside the
/// one root document, <c>BlocwerkApp</c>, so the meta in its <c>&lt;head&gt;</c> is on every server-rendered page. The
/// only other document is the offline client's shell (<c>OfflineClientShell</c>, PR 3.8), which says <c>offline</c>;
/// the structural test pins both, and the render tests pin what the meta says.
/// </summary>
public sealed partial class CachePartitionMetaTests : IDisposable
{
    private readonly string keyDir = Path.Combine(Path.GetTempPath(), "bwk-partition-key", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("u-abcdefghijklmnopqrst")]
    [InlineData("k-abcdefghijklmnopqrst")]
    [InlineData("none")]
    public async Task TheMeta_CarriesThePartition(string partition)
    {
        var resolver = Substitute.For<ICachePartitionResolver>();
        resolver.ResolveAsync().Returns(partition);

        Assert.Equal($"<meta name=\"bw-partition\" content=\"{partition}\" />", (await RenderAsync(resolver)).Trim());
    }

    /// <summary>
    /// An <c>anon</c> page also says why nobody is signed in (PR 3.5): <c>expired</c> by default, the sticky marker's
    /// <c>forced</c> or <c>gone</c>. Only <c>expired</c> lets the device lock its <c>u-</c> partition instead of wiping it.
    /// </summary>
    [Theory]
    [InlineData(null, "expired")]
    [InlineData("forced", "forced")]
    [InlineData("gone", "gone")]
    public async Task AnAnonymousMeta_CarriesTheSignOutReason(string? marker, string expected)
    {
        var resolver = Substitute.For<ICachePartitionResolver>();
        resolver.ResolveAsync().Returns("anon");
        var http = new DefaultHttpContext();
        if (marker is not null)
        {
            http.Request.Headers.Cookie = $"{SignOutReason.MarkerCookie}={marker}";
        }

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);

        Assert.Equal($"<meta name=\"bw-partition\" content=\"anon\" data-signout=\"{expected}\" />", (await RenderAsync(resolver, accessor)).Trim());
    }

    /// <summary>
    /// An undecidable partition says <c>unknown</c> (trust nothing), never <c>offline</c>: a server fault must not wipe
    /// every device on every page, nor keep a cache across a real identity switch.
    /// </summary>
    [Fact]
    public async Task AResolverThatFails_RendersUnknown()
    {
        var resolver = Substitute.For<ICachePartitionResolver>();
        resolver.ResolveAsync().Returns<string>(_ => throw new InvalidOperationException("boom"));

        Assert.Contains("content=\"unknown\"", await RenderAsync(resolver));
    }

    [Fact]
    public void EveryServerRenderedPage_GoesThroughARootDocument_WhoseHeadCarriesTheMeta()
    {
        var web = Path.Combine(RepoRoot(), "src", "Blocwerk.Web");
        var app = File.ReadAllText(Path.Combine(web, "Components", "BlocwerkApp.razor"));
        var head = app[app.IndexOf("<head>", StringComparison.Ordinal)..app.IndexOf("</head>", StringComparison.Ordinal)];
        Assert.Contains("<CachePartitionMeta />", head);

        // The offline client's shell is the one other document. It must say `offline` and never resolve the identity:
        // the service worker precaches it (PR 3.9) and serves the same bytes to whoever opens /v/ offline, so a resolved
        // label would freeze one identity into a shared copy and claim it after a sign-out. `offline` decides nothing
        // (asset-wipe.js); the client takes the partition from the ledger's active label (PR 3.10).
        var shell = File.ReadAllText(Path.Combine(web, "Components", "OfflineClient", "OfflineClientShell.razor"));
        var shellHead = shell[shell.IndexOf("<head>", StringComparison.Ordinal)..shell.IndexOf("</head>", StringComparison.Ordinal)];
        Assert.Contains("<meta name=\"bw-partition\" content=\"offline\" />", shellHead);
        Assert.DoesNotContain("CachePartitionMeta", shell);

        // No other component writes a document of its own, and the app maps exactly one Razor Components root (the shell
        // is a controller's RazorComponentResult, not a second root).
        var documents = Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(f => HeadTag().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["BlocwerkApp.razor", "OfflineClientShell.razor"], documents);
        var program = File.ReadAllText(Path.Combine(web, "Program.cs"));
        Assert.Single(Regex.Matches(program, @"MapRazorComponents<"));
        Assert.Contains("MapRazorComponents<BlocwerkApp>", program);

        Assert.Contains("<meta name=\"bw-partition\" content=\"offline\" />", File.ReadAllText(Path.Combine(web, "wwwroot", "offline.html")));
    }

    [Fact]
    public void TheKey_PersistsProtected_AndSurvivesARestart()
    {
        var file = Path.Combine(keyDir, "partition.key");
        var first = Key(file).Key;

        Assert.Equal(32, first.Length);
        Assert.Equal(first, Key(file).Key);
        Assert.DoesNotContain(Convert.ToBase64String(first), File.ReadAllText(file));
    }

    [Fact]
    public void AnUnreadableKeyFile_IsReplaced_AndNoFileMeansAKeyForThisRunOnly()
    {
        var file = Path.Combine(keyDir, "partition.key");
        Directory.CreateDirectory(keyDir);
        File.WriteAllText(file, "not protected");

        var replaced = Key(file).Key;
        Assert.Equal(replaced, Key(file).Key);
        var bad = Assert.Single(Directory.GetFiles(keyDir, "partition.key.*.bad"));
        Assert.Equal("not protected", File.ReadAllText(bad));
        Assert.NotEqual(Key(null).Key, Key(null).Key);
    }

    /// <summary>A key ring that cannot protect gives a key for this run, never an exception the Lazy would keep.</summary>
    [Fact]
    public void AKeyRingThatThrows_GivesARunKey_NotAnException()
    {
        var protector = Substitute.For<IDataProtector>();
        protector.Protect(Arg.Any<byte[]>()).Returns<byte[]>(_ => throw new System.Security.Cryptography.CryptographicException("ring"));
        var provider = Substitute.For<IDataProtectionProvider>();
        provider.CreateProtector(Arg.Any<string>()).Returns(protector);
        var key = new DataProtectionCachePartitionKey(provider, Path.Combine(keyDir, "partition.key"), NullLogger<DataProtectionCachePartitionKey>.Instance);

        Assert.Equal(32, key.Key.Length);
        Assert.Equal(key.Key, key.Key);
        Assert.False(File.Exists(Path.Combine(keyDir, "partition.key")));
    }

    public void Dispose()
    {
        if (Directory.Exists(keyDir))
        {
            Directory.Delete(keyDir, recursive: true);
        }
    }

    private DataProtectionCachePartitionKey Key(string? file) => new(
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(keyDir, "ring"))),
        file,
        NullLogger<DataProtectionCachePartitionKey>.Instance);

    private static async Task<string> RenderAsync(ICachePartitionResolver resolver, IHttpContextAccessor? accessor = null)
    {
        var collection = new ServiceCollection().AddSingleton(resolver);
        if (accessor is not null)
        {
            collection.AddSingleton(accessor);
        }

        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<CachePartitionMeta>(ParameterView.Empty)).ToHtmlString());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    [GeneratedRegex(@"<head[\s>]")]
    private static partial Regex HeadTag();
}
