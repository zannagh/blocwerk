// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// Who may list, add and delete comments: the boulder's own visibility for reading, members for writing, and a kiosk
/// acting as a member only on its wall. A signed-in user used to be able to comment on ANY boulder in the
/// installation and anyone could list any boulder's comments. SQLite; see <see cref="PostgresCommentAccessTests"/>.
/// </summary>
public class CommentAccessTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task List_FollowsTheBoulder_DraftsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await CommentAccessChecks.ListAsync(h, viewer);
    }

    [Fact]
    public async Task List_IsEmpty_ForAStrangerAnAnonymousVisitorAndAWrongToken()
    {
        using var h = new WallTestHarness();
        await CommentAccessChecks.ListForStrangersAsync(h);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task Add_IsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await CommentAccessChecks.AddAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task Delete_IsForTheAuthor_OnAWallTheyCanSee(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await CommentAccessChecks.DeleteAsync(h, viewer);
    }

    [Fact]
    public async Task KioskActingAsAMember_CommentsOnlyOnTheTabletsWall()
    {
        using var h = new WallTestHarness();
        await CommentAccessChecks.KioskActAsStaysOnItsWallAsync(h);
    }
}
