// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Keeps <see cref="Assets.AssetUrls"/> the one builder of the hashable asset URLs: no component builds a live panel
/// photo, wall photo, gallery, texture, splat or video-thumbnail URL by hand any more. What may still be built by hand
/// is listed with its reason; anything new has to go through <c>AssetUrls</c> so it picks up the hashed form.
/// </summary>
public partial class AssetUrlGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// File and the reason it may still name a legacy route itself. The API documentation (<c>ApiDocsData*</c>) names
    /// routes as text, never as URLs to load, and is skipped too.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        // The retiring single-image arms: the default photo of the zero-panel editor, and the picker's fallback when
        // no panel loaded. Both are on their way out with Wall.Photo.
        ["src/Blocwerk.Web/Components/Shared/WallPhotoEditor.razor"] = "retiring single-image wall photo",
        ["src/Blocwerk.Web/Components/Shared/HoldPicker.razor"] = "fallback when no panel loaded",
    };

    [Fact]
    public void NoComponentBuildsAHashableAssetUrlByHand()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "Blocwerk.Web", "Components"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(RepoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (Allowed.ContainsKey(relative) || relative.Contains("/ApiDocsData", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file).Where(l => !IsComment(l)))
            {
                if (HandBuilt().IsMatch(line))
                {
                    offenders.Add($"{relative}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// A hashed URL must never be built from an entity's own hash column: the circuit-cached Wall (WallCacheState)
    /// carries PhotoSha256, which the privacy rewrite moves by ExecuteUpdate without evicting the circuit. Hashes reach
    /// <c>AssetUrls</c> only from database projections (WallPanelInfo, gallery and video rows, IAssetHashReader).
    /// </summary>
    [Fact]
    public void NoComponentHandsAnEntitysHashColumnToAssetUrls()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "Blocwerk.Web"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select(l => (File: f, Line: l)))
            .Where(x => x.Line.Contains("Sha256", StringComparison.Ordinal) && x.Line.Contains("AssetUrls.", StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(RepoRoot, x.File)}: {x.Line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("@*", StringComparison.Ordinal);
    }

    /// <summary>
    /// A live panel photo (<c>/panels/{id}/photo</c>, not <c>staged-photo</c>), a wall photo (<c>/photo</c> or
    /// <c>/photo/{generation}</c>), a gallery, texture or splat route, or a video thumbnail, inside a string.
    /// </summary>
    [GeneratedRegex("""["$]/?(?:api|media)/walls/[^"]*/(?:panels/[^"/]+/photo\b|photo(?:["/?{]|$)|gallery/|geometry/)|/api/beta-videos/[^"]*/thumbnail""")]
    private static partial Regex HandBuilt();

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
