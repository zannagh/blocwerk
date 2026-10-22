// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="CommentAccessTests"/> on PostgreSQL, so the gates are translated by the real provider.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresCommentAccessTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task List_FollowsTheBoulder_DraftsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await CommentAccessChecks.ListAsync(h, viewer);
    }

    [PostgresFact]
    public async Task List_IsEmpty_ForAStrangerAnAnonymousVisitorAndAWrongToken()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await CommentAccessChecks.ListForStrangersAsync(h);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task Add_IsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await CommentAccessChecks.AddAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task Delete_IsForTheAuthor_OnAWallTheyCanSee(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await CommentAccessChecks.DeleteAsync(h, viewer);
    }

    [PostgresFact]
    public async Task KioskActingAsAMember_CommentsOnlyOnTheTabletsWall()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await CommentAccessChecks.KioskActAsStaysOnItsWallAsync(h);
    }
}
