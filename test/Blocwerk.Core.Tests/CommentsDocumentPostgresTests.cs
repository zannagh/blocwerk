// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.DataDocumentFreshnessTests;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The <c>comments</c> document on Postgres, with real transaction ids: the class matrix, a comment still in flight while
/// the build reads (missed, then rebuilt once it commits), and an erasure's resync.
/// </summary>
public sealed class CommentsDocumentPostgresTests
{
    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task TheClasses_OnPostgres_SettersReadTheDraftsComment_TheKioskActingAsASetterDoesNot()
    {
        using var f = await SeededAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        f.Caller = f.Setter;
        Assert.Contains((await CommentsDocumentSeed.DocumentAsync(f)).Comments, c => c.Id == seed.OnDraft);
        var settersSha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Comments);

        f.BindKiosk(f.WallId);
        Assert.Equal(DocumentAudience.Kiosk, (await f.AccessAsync()).Audience);
        var kiosk = await CommentsDocumentSeed.DocumentAsync(f);
        Assert.Equal(seed.Everyones.Order(), kiosk.Comments.Select(c => c.Id).Order());
        Assert.Null(kiosk.Comments.Single(c => c.Id == seed.ByMember).Author.Id);
        Assert.Equal(404, (await f.DocumentHttpAsync(DataDocumentKinds.Comments, settersSha)).Response.StatusCode);
    }

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task ACommentInFlightDuringTheBuild_IsMissed_ThenRebuiltOnceItCommits()
    {
        using var f = await SeededAsync();
        f.Caller = f.Member;
        await f.IndexAsync();
        await using var writer = StructureFeedKit.Context(f.Harness);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        var comment = new BoulderComment { BoulderId = f.PublishedId, UserId = f.Member.Id, Text = "in flight" };
        writer.BoulderComments.Add(comment);
        await writer.SaveChangesAsync();

        var during = await f.IndexAsync();
        Assert.DoesNotContain((await CommentsDocumentSeed.DocumentAsync(f)).Comments, c => c.Id == comment.Id);
        await transaction.CommitAsync();
        var row = await writer.WallChanges.AsNoTracking().SingleAsync(c => c.Kind == FeedKinds.CommentAdded && c.EntityId == comment.Id);
        var after = await f.IndexAsync();

        Assert.True(Position(Entry(during, DataDocumentKinds.Comments).Cursor) < FeedPosition.Of(row));
        Assert.NotEqual(DataDocumentFixture.Sha(during, DataDocumentKinds.Comments), DataDocumentFixture.Sha(after, DataDocumentKinds.Comments));
        Assert.Equal("in flight", (await CommentsDocumentSeed.DocumentAsync(f)).Comments.Single(c => c.Id == comment.Id).Text);
    }

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task AnErasure_OnPostgres_RebuildsTheComments_WithThePlaceholder()
    {
        using var f = await SeededAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        f.Caller = f.Setter;
        await f.IndexAsync();
        f.Harness.ActingUser = f.Member;

        Assert.True(await DeletionFixture.CreateService(f.Harness, f.Harness.BetaStorage).DeleteAsync(f.Member.Id));

        var author = (await CommentsDocumentSeed.DocumentAsync(f)).Comments.Single(c => c.Id == seed.ByMember).Author;
        Assert.Equal(Helpers.PlaceholderIdentity.DisplayName, author.Name);
        Assert.DoesNotContain(f.Member.Id.ToString(), await CommentsDocumentSeed.TextAsync(f), StringComparison.Ordinal);
    }

    private static async Task<DataDocumentFixture> SeededAsync()
    {
        var f = new DataDocumentFixture(PostgresTestDatabase.Create());
        await f.SeedAsync();
        return f;
    }
}
