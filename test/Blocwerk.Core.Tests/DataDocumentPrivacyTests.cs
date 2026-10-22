// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Blocwerk.Contracts.Documents;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What may never be in a data document: anything about the reader (a source scan of the contracts and the builders, so
/// a field added later is caught before it ships), and a draft in any class outside the wall's members (share links, kiosks).
/// </summary>
public sealed partial class DataDocumentPrivacyTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>The document files that build content; the gate and the service decide the class and are not builders.</summary>
    private static readonly string[] BuilderFiles =
    [
        "WallDocumentBuilder.cs", "HoldsDocumentBuilder.cs", "BouldersDocumentBuilder.cs", "GalleryDocumentBuilder.cs",
        "BoulderStatsCounts.cs", "BoulderRow.cs", "DocumentPoints.cs", "DataDocumentBuilder.cs",
    ];

    [Fact]
    public void NoDocumentContract_CarriesAPerUserOrPerViewerProperty()
    {
        var types = typeof(DataDocumentKinds).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(DataDocumentKinds).Namespace && t.IsPublic)
            .ToList();
        Assert.True(types.Count >= 10, "the scan found the contracts");

        var offending = types
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{t.Name}.{p.Name}"))
            .Where(name => PerUser().IsMatch(name.Split('.')[1]))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void TheContractAndBuilderSources_NeverReadTheReader()
    {
        var contracts = Directory.GetFiles(Path.Combine(RepoRoot, "src", "Blocwerk.Contracts", "Documents"), "*.cs");
        var builders = BuilderFiles.Select(f => Path.Combine(RepoRoot, "src", "Blocwerk.Core", "Assets", "DataDocs", f)).ToArray();
        Assert.True(contracts.Length >= 10);
        Assert.All(builders, f => Assert.True(File.Exists(f), f));

        var offending = contracts.Concat(builders)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal) && ReadsTheReader().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public async Task OnlyMembersAndSetters_SeeTheDraft_InAnyDocument_AndNoDocumentNamesTheShareToken()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var members = new[]
        {
            await AllBytesAsync(f, () => f.Caller = f.Setter),
            await AllBytesAsync(f, () => f.Caller = f.Member),
        };
        Assert.All(members, t => Assert.Contains(f.DraftId.ToString(), t, StringComparison.Ordinal));
        Assert.All(members, t => Assert.Contains("Secret draft", t, StringComparison.Ordinal));

        var others = new[]
        {
            await AllBytesAsync(f, () => f.Caller = null, DataDocumentFixture.ShareToken),
            await AllBytesAsync(f, () => f.Caller = new Entities.User { Identifier = "stranger@test" }, DataDocumentFixture.ShareToken),
            await AllBytesAsync(f, () => Kiosk(f, actingAs: null)),
            await AllBytesAsync(f, () => Kiosk(f, actingAs: f.Setter)),
        };
        foreach (var text in others)
        {
            Assert.DoesNotContain(f.DraftId.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret draft", text, StringComparison.Ordinal);
        }

        Assert.All(others.Concat(members), t => Assert.DoesNotContain(DataDocumentFixture.ShareToken, t, StringComparison.Ordinal));
        foreach (var userId in new[] { f.Member.Id, f.Setter.Id, f.Harness.Owner.Id })
        {
            Assert.All(others.Concat(members), t => Assert.DoesNotContain(userId.ToString(), t, StringComparison.Ordinal));
        }
    }

    // Property names that would describe the reader: own marks, the session, the share link, the partition.
    [GeneratedRegex(@"^(Is)?(My|Mine|Own|Favou?rite|Tick|Attempt|Done|Viewer|User|Token|Email|Partition|Member|Caller)", RegexOptions.IgnoreCase)]
    private static partial Regex PerUser();

    [GeneratedRegex(@"ICurrentUserService|CurrentUserId|GetCurrentUser|BoulderFavorites|ShareToken|UserId\s*==|==\s*\w*[uU]serId|FeedSession|IKioskContext|HttpContext")]
    private static partial Regex ReadsTheReader();

    private static void Kiosk(DataDocumentFixture f, Entities.User? actingAs)
    {
        f.Caller = actingAs;
        f.BindKiosk(f.WallId);
    }

    private static async Task<string> AllBytesAsync(DataDocumentFixture f, Action who, string? token = null)
    {
        who();
        var index = await f.IndexAsync(token);
        var access = await f.AccessAsync(token);
        var text = new StringBuilder();
        foreach (var doc in index.Docs)
        {
            var stored = await f.Service().GetAsync(f.WallId, access.Audience!.Value, doc.Kind, doc.Sha, default);
            text.Append(Encoding.UTF8.GetString(stored!.Json));
        }

        return text.ToString();
    }

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
