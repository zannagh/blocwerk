// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The kiosk class's comments document names its authors as the resting tablet does (<see cref="KioskBylines"/>): members
/// who consented to the kiosk by name, everyone else as "A member" with no id and no avatar, whoever the tablet acts as.
/// Consent writes no feed row, so a consent given or withdrawn since the build must rebuild the document on its own.
/// </summary>
public sealed class CommentsDocumentKioskBylinesTests
{
    [Fact]
    public async Task TheKioskClass_NamesConsentingMembersOnly_TheOthersReadAsAMember()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        await SetConsentAsync(f, f.Harness.Owner.Id, consented: true);
        var ownerName = await NameAsync(f, f.Harness.Owner.Id);

        foreach (var (who, token) in CommentsDocumentAccessTests.Kiosks(f))
        {
            who();
            var document = await CommentsDocumentSeed.DocumentAsync(f, token);
            var text = await CommentsDocumentSeed.TextAsync(f, token);

            var member = document.Comments.Single(c => c.Id == seed.ByMember).Author;
            Assert.Equal((KioskBylines.PlaceholderName, false, null), (member.Name, member.HasAvatar, member.Id));
            Assert.DoesNotContain("member@test", text, StringComparison.Ordinal);
            Assert.DoesNotContain(f.Member.Id.ToString(), text, StringComparison.Ordinal);

            var owner = document.Comments.Single(c => c.Id == seed.ByOwner).Author;
            Assert.Equal((ownerName, true, f.Harness.Owner.Id), (owner.Name, owner.HasAvatar, owner.Id));

            foreach (var id in new[] { seed.ByErased, seed.ByGhost })
            {
                Assert.Equal(PlaceholderIdentity.DisplayName, document.Comments.Single(c => c.Id == id).Author.Name);
            }
        }
    }

    [Fact]
    public async Task ShareLinks_StillNameEveryMember_TheMaskIsTheKiosksOnly()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);

        f.Caller = null;
        var document = await CommentsDocumentSeed.DocumentAsync(f, DataDocumentFixture.ShareToken);

        Assert.Equal("member@test", document.Comments.Single(c => c.Id == seed.ByMember).Author.Name);
    }

    [Fact]
    public async Task AConsentGivenOrWithdrawnSinceTheBuild_RebuildsTheKioskDocument()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        var seed = await CommentsDocumentSeed.AddAsync(f);
        f.Caller = null;
        f.BindKiosk(f.WallId);

        var before = await CommentsDocumentSeed.DocumentAsync(f);
        Assert.Equal(KioskBylines.PlaceholderName, before.Comments.Single(c => c.Id == seed.ByMember).Author.Name);

        await SetConsentAsync(f, f.Member.Id, consented: true);
        var given = await CommentsDocumentSeed.DocumentAsync(f);
        Assert.Equal("member@test", given.Comments.Single(c => c.Id == seed.ByMember).Author.Name);

        await SetConsentAsync(f, f.Member.Id, consented: false);
        var withdrawn = await CommentsDocumentSeed.DocumentAsync(f);
        Assert.Equal(KioskBylines.PlaceholderName, withdrawn.Comments.Single(c => c.Id == seed.ByMember).Author.Name);
    }

    private static async Task SetConsentAsync(DataDocumentFixture f, Guid userId, bool consented)
    {
        await using var db = f.Harness.CreateContext();
        var member = await db.WallMembers.IgnoreQueryFilters().SingleAsync(m => m.WallId == f.WallId && m.UserId == userId);
        member.KioskConsentedAt = consented ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync();
    }

    private static async Task<string> NameAsync(DataDocumentFixture f, Guid userId)
    {
        await using var db = f.Harness.CreateContext();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        return string.IsNullOrWhiteSpace(user.CustomDisplayName) ? user.DisplayName : user.CustomDisplayName;
    }
}
