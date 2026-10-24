// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.CompilerServices;
using Blocwerk.Web.State;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall page's per-circuit memory (back renders the page as it was left) and the page's rules around it: a head
/// start that is dropped when it gets old or the wall is gone, and never something the editor works on.
/// </summary>
public class WallPageMemoryTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public async Task Recall_ReturnsTheRememberedPage_ForTheSameWallAndShareTokenOnly()
    {
        await using var f = new CircuitCacheFixture();
        using var memory = new WallPageMemory(f.Cache, new ManualTime());
        var wall = Guid.NewGuid();
        var page = new object();

        memory.Remember(wall, null, page);

        Assert.Same(page, memory.Recall<object>(wall, null));
        Assert.Null(memory.Recall<object>(wall, "token"));
        Assert.Null(memory.Recall<object>(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task OnlyTheMostRecentlyLeftWallsAreKept_AndForgetDropsAWall()
    {
        await using var f = new CircuitCacheFixture();
        using var memory = new WallPageMemory(f.Cache, new ManualTime());
        var walls = Enumerable.Range(0, WallPageMemory.Capacity + 1).Select(_ => Guid.NewGuid()).ToList();
        foreach (var wall in walls)
        {
            memory.Remember(wall, null, wall.ToString());
        }

        Assert.Null(memory.Recall<string>(walls[0], null));
        Assert.Equal(walls[^1].ToString(), memory.Recall<string>(walls[^1], null));

        memory.Forget(walls[^1]);
        Assert.Null(memory.Recall<string>(walls[^1], null));
    }

    [Fact]
    public async Task AnOldPage_IsNotRecalled()
    {
        await using var f = new CircuitCacheFixture();
        var time = new ManualTime();
        using var memory = new WallPageMemory(f.Cache, time);
        var wall = Guid.NewGuid();
        memory.Remember(wall, null, "page");

        time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal("page", memory.Recall<string>(wall, null));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(memory.Recall<string>(wall, null));
    }

    /// <summary>
    /// The restored page is on screen while its refresh runs, and that refresh replaces the holds the editor drags:
    /// entering edit mode waits for it. A deleted wall is forgotten before leaving, and remembering runs last in
    /// Dispose, behind a catch, so it can never keep the edit lease from being released.
    /// </summary>
    [Fact]
    public void ThePage_DefersEditingUntilRefreshed_ForgetsADeletedWall_AndRemembersLastOnDispose()
    {
        var markup = ReadSource("src/Blocwerk.Web/Components/Pages/Walls/WallDetail.razor");
        var memory = ReadSource("src/Blocwerk.Web/Components/Pages/Walls/WallDetail.PageMemory.cs");

        var toggle = markup.IndexOf("private async Task ToggleEditing()", StringComparison.Ordinal);
        var defer = markup.IndexOf("if (!_isEditing && DeferEditUntilRefreshed())", toggle, StringComparison.Ordinal);
        Assert.True(toggle > 0 && defer > toggle && defer < markup.IndexOf("_isEditing = !_isEditing;", toggle, StringComparison.Ordinal));
        Assert.Contains("var restored = RestoreFromPageMemory();", markup);
        Assert.Contains("await RefreshRestoredPageAsync(restored, LoadCurrentWall);", markup);
        Assert.Contains("_refreshing = restored;", memory);

        // The deferred tap: always consumed, replayed only on a live page that shows this user the edit button.
        Assert.Contains("private bool MayEnterEditNow() => !_disposed && _wall is not null && _canEditHolds && !_isEditing && !_show3D;", memory);
        Assert.Contains("if (wanted && MayEnterEditNow())", memory);
        var replay = memory.IndexOf("private async Task RefreshRestoredPageAsync", StringComparison.Ordinal);
        var consumed = memory.IndexOf("_editAfterRefresh = false;", replay, StringComparison.Ordinal);
        Assert.True(consumed > memory.IndexOf("finally", replay, StringComparison.Ordinal) && consumed < memory.IndexOf("if (wanted", replay, StringComparison.Ordinal));
        Assert.Contains("if (_isEditing && !_disposed)", markup);
        Assert.Contains("finally\n        {\n            // Consumed whatever happened: a failed refresh must not leave a tap to be replayed later.\n            _refreshing = false;", memory.Replace("\r\n", "\n"));

        var delete = markup.IndexOf("private async Task DeleteWall()", StringComparison.Ordinal);
        var forget = markup.IndexOf("ForgetPage();", delete, StringComparison.Ordinal);
        Assert.True(forget > delete && forget < markup.IndexOf("Navigation.NavigateTo(\"/walls\");", delete, StringComparison.Ordinal));
        Assert.Contains("_pageGone ||", memory);

        var dispose = markup.IndexOf("public void Dispose()", StringComparison.Ordinal);
        var end = markup.IndexOf("\n    }\n", dispose, StringComparison.Ordinal);
        Assert.True(markup.IndexOf("_disposed = true;", dispose, StringComparison.Ordinal) < markup.IndexOf("Cache.Changed -= OnCacheChanged;", dispose, StringComparison.Ordinal), "the flag is set first");
        var remember = markup.IndexOf("RememberPageSafely();", dispose, StringComparison.Ordinal);
        Assert.True(remember > markup.IndexOf("_editLease?.Dispose();", dispose, StringComparison.Ordinal) && remember < end);
        Assert.Contains("catch (Exception ex)", memory);
    }

    private static string ReadSource(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

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
