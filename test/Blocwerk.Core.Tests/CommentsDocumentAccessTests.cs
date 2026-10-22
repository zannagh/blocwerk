// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Who reads which comments, and as whom their authors appear: the #180 read gate per class (drafts' comments for the
/// wall's members and setters only, as the class's <c>boulders</c> document lists them: drafts' and archived boulders'
/// comments for members and setters only, historic ones for every class), and the feed's byline rule (erased and
/// Ghost authors as the placeholder; author ids for members, otherwise only where an avatar shows one).
/// </summary>
public sealed class CommentsDocumentAccessTests
{
    [Fact]
    public async Task MembersAndSetters_ReadEveryCommentOfTheWall_TheDraftsToo_WithFullText_AndNoOtherWalls()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);

        foreach (var reader in new[] { f.Setter, f.Member, f.Harness.Owner })
        {
            f.Caller = reader;
            var document = await CommentsDocumentSeed.DocumentAsync(f);

            Assert.Equal(seed.Everyones.Append(seed.OnDraft).Append(seed.OnArchived).Order(), document.Comments.Select(c => c.Id).Order());
            Assert.Equal("draft secret: the start is wrong", document.Comments.Single(c => c.Id == seed.OnDraft).Text);
            Assert.Equal("archived but remembered", document.Comments.Single(c => c.Id == seed.OnArchived).Text);
            Assert.Equal(f.Member.Id, document.Comments.Single(c => c.Id == seed.ByMember).Author.Id);
            Assert.DoesNotContain(document.Comments, c => c.Id == seed.OnOtherWall);
        }
    }

    [Fact]
    public async Task ShareLinksAndKiosks_ReadNoDraftsOrArchivedComment_ButHistoricOnes_AsTheirBouldersDocumentLists()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);

        foreach (var (who, token) in Outsiders(f))
        {
            who();
            var text = await CommentsDocumentSeed.TextAsync(f, token);
            var document = await CommentsDocumentSeed.DocumentAsync(f, token);

            Assert.Equal(seed.Everyones.Order(), document.Comments.Select(c => c.Id).Order());
            Assert.DoesNotContain(seed.OnDraft.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain("draft secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain(seed.OnArchived.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain("archived but remembered", text, StringComparison.Ordinal);
            Assert.Contains(seed.ByErased, document.Comments.Select(c => c.Id));
            Assert.DoesNotContain(seed.OnOtherWall.ToString(), text, StringComparison.Ordinal);
        }
    }

    /// <summary>The author id goes to members; outside the wall only for the owner, whose avatar URL shows it anyway.</summary>
    [Fact]
    public async Task OutsideTheWall_AnAuthorsIdIsShownOnlyWithAnAvatar_InsideAlways()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);

        foreach (var (who, token) in Outsiders(f))
        {
            who();
            var document = await CommentsDocumentSeed.DocumentAsync(f, token);
            var text = await CommentsDocumentSeed.TextAsync(f, token);

            Assert.Null(document.Comments.Single(c => c.Id == seed.ByMember).Author.Id);
            Assert.Equal("member@test", document.Comments.Single(c => c.Id == seed.ByMember).Author.Name);
            var owner = document.Comments.Single(c => c.Id == seed.ByOwner).Author;
            Assert.Equal((f.Harness.Owner.Id, true), (owner.Id!.Value, owner.HasAvatar));
            Assert.DoesNotContain(f.Member.Id.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain(GhostUser.Id.ToString(), text, StringComparison.Ordinal);
        }

        f.Kiosk.IsKiosk.Returns(false);
        f.Caller = f.Member;
        var inside = await CommentsDocumentSeed.DocumentAsync(f);
        Assert.Equal(f.Member.Id, inside.Comments.Single(c => c.Id == seed.ByMember).Author.Id);
    }

    [Fact]
    public async Task ErasedAndGhostAuthors_ArePlaceholders_InEveryClass_AndTheErasedIdIsNeverWritten()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);

        // Inside first: the kiosk binding stays on the fixture once made.
        IEnumerable<(Action Who, string? Token)> inside = [(() => f.Caller = f.Member, null), (() => f.Caller = f.Setter, null)];
        var classes = inside.Concat(Outsiders(f));

        foreach (var (who, token) in classes)
        {
            who();
            var document = await CommentsDocumentSeed.DocumentAsync(f, token);
            var text = await CommentsDocumentSeed.TextAsync(f, token);

            foreach (var id in new[] { seed.ByErased, seed.ByGhost })
            {
                var author = document.Comments.Single(c => c.Id == id).Author;
                Assert.Equal((PlaceholderIdentity.DisplayName, false), (author.Name, author.HasAvatar));
                Assert.True(author.Id is null || author.Id == GhostUser.Id);
            }

            Assert.Equal("said before leaving", document.Comments.Single(c => c.Id == seed.ByErased).Text);
            Assert.DoesNotContain(CommentsDocumentSeed.ErasedName, text, StringComparison.Ordinal);
            Assert.DoesNotContain(seed.ErasedId.ToString(), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheClassRules_DraftAndArchivedCommentsAndAuthorIds_AreTheMembersAndSettersOnly()
    {
        var author = new Feed.FeedAuthor(Guid.NewGuid(), "A", HasAvatar: false, IsDeleted: false);
        foreach (var audience in Enum.GetValues<DocumentAudience>())
        {
            var inside = audience is DocumentAudience.Setters or DocumentAudience.Members;
            Assert.Equal(inside, DocumentAudiences.SeesDrafts(audience));
            Assert.Equal(inside, DocumentAudiences.SeesArchived(audience));
            Assert.Equal(inside, DocumentAudiences.Authors(audience).Describe(author).Id is not null);
        }
    }

    // A share link (signed out, and signed in as a non-member) and a kiosk tablet (resting, and acting as a setter).
    private static IEnumerable<(Action Who, string? Token)> Outsiders(DataDocumentFixture f) =>
    [
        (() => f.Caller = null, DataDocumentFixture.ShareToken),
        (() => f.Caller = new User { Identifier = "stranger@test" }, DataDocumentFixture.ShareToken),
        (() => Kiosk(f, actingAs: null), null),
        (() => Kiosk(f, actingAs: f.Setter), null),
    ];

    private static void Kiosk(DataDocumentFixture f, User? actingAs)
    {
        f.Caller = actingAs;
        f.BindKiosk(f.WallId);
    }
}
