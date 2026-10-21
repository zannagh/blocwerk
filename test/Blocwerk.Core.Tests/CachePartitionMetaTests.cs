// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.RegularExpressions;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The <c>bw-partition</c> meta (design §6.6) and the key behind the labels. Every <c>@page</c> is rendered inside the
/// one root document, <c>BlocwerkApp</c>, so the meta in its <c>&lt;head&gt;</c> is on every server-rendered page; the
/// structural test pins that, and the render tests pin what the meta says.
/// </summary>
public sealed partial class CachePartitionMetaTests : IDisposable
{
    private readonly string keyDir = Path.Combine(Path.GetTempPath(), "bwk-partition-key", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("u-abcdefghijklmnopqrst")]
    [InlineData("anon")]
    [InlineData("k-abcdefghijklmnopqrst")]
    [InlineData("none")]
    public async Task TheMeta_CarriesThePartition(string partition)
    {
        var resolver = Substitute.For<ICachePartitionResolver>();
        resolver.ResolveAsync().Returns(partition);

        Assert.Equal($"<meta name=\"bw-partition\" content=\"{partition}\" />", (await RenderAsync(resolver)).Trim());
    }

    /// <summary>An undecidable partition says <c>none</c>, which wipes: never a stale label that would keep a cache.</summary>
    [Fact]
    public async Task AResolverThatFails_RendersNone()
    {
        var resolver = Substitute.For<ICachePartitionResolver>();
        resolver.ResolveAsync().Returns<string>(_ => throw new InvalidOperationException("boom"));

        Assert.Contains("content=\"none\"", await RenderAsync(resolver));
    }

    [Fact]
    public void EveryServerRenderedPage_GoesThroughTheRootDocument_WhoseHeadCarriesTheMeta()
    {
        var web = Path.Combine(RepoRoot(), "src", "Blocwerk.Web");
        var app = File.ReadAllText(Path.Combine(web, "Components", "BlocwerkApp.razor"));
        var head = app[app.IndexOf("<head>", StringComparison.Ordinal)..app.IndexOf("</head>", StringComparison.Ordinal)];
        Assert.Contains("<CachePartitionMeta />", head);

        // No other component writes a document of its own, and the app maps exactly one root.
        var documents = Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(f => HeadTag().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["BlocwerkApp.razor"], documents);
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
        Assert.NotEqual(Key(null).Key, Key(null).Key);
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

    private static async Task<string> RenderAsync(ICachePartitionResolver resolver)
    {
        await using var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
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
