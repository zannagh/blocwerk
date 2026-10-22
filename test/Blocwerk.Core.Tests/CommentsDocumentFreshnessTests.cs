// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// How a cached <c>comments</c> document learns of a change, through the real feed interceptor: a deleted comment, a
/// deleted boulder (its comments cascade with no rows of their own), a published draft, an author renamed after leaving
/// the wall, and an erasure (its <c>wall.resync</c>) each give the next index a new sha, built without what is gone.
/// </summary>
public sealed class CommentsDocumentFreshnessTests
{
    [Fact]
    public Task ADeletedComment_GivesANewSha_WithoutIt_Sqlite() => ADeletedComment_GivesANewSha_WithoutItAsync(new DataDocumentFixture());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task ADeletedComment_GivesANewSha_WithoutIt_Postgres() =>
        ADeletedComment_GivesANewSha_WithoutItAsync(new DataDocumentFixture(PostgresTestDatabase.Create()));

    [Fact]
    public async Task ADeletedBoulder_DropsItsComments_ForEveryClass()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        f.Caller = f.Member;
        var before = await ShaAsync(f);
        var outside = await ShaAsync(f, DataDocumentFixture.ShareToken, signedIn: false);

        await using (var db = StructureFeedKit.Context(f.Harness))
        {
            db.Boulders.Remove(await db.Boulders.SingleAsync(b => b.Id == f.PublishedId));
            await db.SaveChangesAsync();
        }

        f.Caller = f.Member;
        Assert.NotEqual(before, await ShaAsync(f));
        var ids = (await CommentsDocumentSeed.DocumentAsync(f)).Comments.Select(c => c.Id).ToList();
        Assert.DoesNotContain(seed.ByMember, ids);
        Assert.DoesNotContain(seed.ByOwner, ids);
        Assert.Contains(seed.OnDraft, ids);
        Assert.NotEqual(outside, await ShaAsync(f, DataDocumentFixture.ShareToken, signedIn: false));
        Assert.DoesNotContain(seed.ByMember, (await CommentsDocumentSeed.DocumentAsync(f, DataDocumentFixture.ShareToken)).Comments.Select(c => c.Id));
    }

    [Fact]
    public async Task APublishedDraft_OpensItsComments_ToShareLinks()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        f.Caller = null;
        Assert.DoesNotContain(seed.OnDraft, (await CommentsDocumentSeed.DocumentAsync(f, DataDocumentFixture.ShareToken)).Comments.Select(c => c.Id));

        await using (var db = StructureFeedKit.Context(f.Harness))
        {
            (await db.Boulders.SingleAsync(b => b.Id == f.DraftId)).IsDraft = false;
            await db.SaveChangesAsync();
        }

        Assert.Contains(seed.OnDraft, (await CommentsDocumentSeed.DocumentAsync(f, DataDocumentFixture.ShareToken)).Comments.Select(c => c.Id));
    }

    /// <summary>An author who left the wall is still its byline; their rename writes only their own row, on no wall.</summary>
    [Fact]
    public async Task ARenameOfAnAuthorWhoLeftTheWall_RebuildsTheComments()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        f.Caller = f.Setter;
        await CommentsDocumentSeed.DocumentAsync(f);
        await using (var db = StructureFeedKit.Context(f.Harness))
        {
            await db.WallMembers.Where(m => m.UserId == f.Member.Id && m.WallId == f.WallId).ExecuteDeleteAsync();
            (await db.Users.SingleAsync(u => u.Id == f.Member.Id)).CustomDisplayName = "Renamed Member";
            await db.SaveChangesAsync();
        }

        var document = await CommentsDocumentSeed.DocumentAsync(f);

        Assert.Equal("Renamed Member", document.Comments.Single(c => c.Id == seed.ByMember).Author.Name);
    }

    [Fact]
    public async Task AnErasure_ResyncsTheWall_AndTheRebuiltDocumentShowsThePlaceholder()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        f.Caller = f.Setter;
        var before = await ShaAsync(f);
        f.Harness.ActingUser = f.Member;

        Assert.True(await DeletionFixture.CreateService(f.Harness, f.Harness.BetaStorage).DeleteAsync(f.Member.Id));

        Assert.NotEqual(before, await ShaAsync(f));
        var text = await CommentsDocumentSeed.TextAsync(f);
        var author = (await CommentsDocumentSeed.DocumentAsync(f)).Comments.Single(c => c.Id == seed.ByMember).Author;
        Assert.Equal((GhostUser.Id, PlaceholderIdentity.DisplayName), (author.Id!.Value, author.Name));
        Assert.DoesNotContain(f.Member.Id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("member@test", text, StringComparison.Ordinal);
    }

    private static async Task ADeletedComment_GivesANewSha_WithoutItAsync(DataDocumentFixture f)
    {
        using var fixture = f;
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        f.Caller = f.Member;
        var before = await ShaAsync(f);

        await using (var db = StructureFeedKit.Context(f.Harness))
        {
            db.BoulderComments.Remove(await db.BoulderComments.SingleAsync(c => c.Id == seed.ByMember));
            await db.SaveChangesAsync();
        }

        var after = await ShaAsync(f);
        Assert.NotEqual(before, after);
        var text = await CommentsDocumentSeed.TextAsync(f);
        Assert.DoesNotContain(seed.ByMember.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("the crimp is sharp", text, StringComparison.Ordinal);
        await using var check = f.Harness.CreateContext();
        Assert.True(await check.WallChanges.AnyAsync(c => c.Kind == FeedKinds.CommentDeleted && c.EntityId == seed.ByMember));
    }

    private static async Task<string> ShaAsync(DataDocumentFixture f, string? token = null, bool signedIn = true)
    {
        if (!signedIn)
        {
            f.Caller = null;
        }

        return DataDocumentFixture.Sha(await f.IndexAsync(token), DataDocumentKinds.Comments);
    }
}
