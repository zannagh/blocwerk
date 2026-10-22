// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Whose name the kiosk tablet with nobody picked shows on a boulder's comments and activity (<see cref="KioskBylines"/>),
/// shared by the SQLite and the PostgreSQL runs: a member who consented to the kiosk by name, any other member as
/// "A member" with no id and no avatar, the setter of a created or revised entry by name either way, an erased author as
/// Ghost. Acting as a member, and on a share link, the names are what they were.
/// </summary>
internal static class KioskBylineChecks
{
    public static async Task BylinesAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var cora = await ConsentingMemberAsync(h, "cora@test");
        var erased = await ErasedUserAsync(h);
        var log = await SeedAsync(h, s, cora, erased);
        var masks = viewer == DraftViewer.KioskAnonymous;
        var member = masks ? KioskBylines.PlaceholderName : s.Member.Name;

        var comments = (await s.Comments(viewer).GetCommentsAsync(s.LiveId, shareToken: s.TokenFor(viewer))).Items;
        Assert.Equal(
            [("from cora", cora.Name), ("from the erased", PlaceholderIdentity.DisplayName), ("from the member", member)],
            comments.Select(c => (c.Text, c.User.Name)).OrderBy(x => x.Text));

        var activity = (await s.Activity(viewer).GetBoulderActivityAsync(s.LiveId, pageSize: 10, shareToken: s.TokenFor(viewer)))
            .Items.ToDictionary(a => a.Id);
        Assert.Equal(cora.Name, activity[log.Cora].User.Name);
        Assert.Equal(member, activity[log.Member].User.Name);
        Assert.Equal(s.Member.Name, activity[log.Created].User.Name); // the setter, consent or not
        Assert.Equal(PlaceholderIdentity.DisplayName, activity[log.Erased].User.Name);

        // A masked author carries no trace of who they were: no id, no avatar (the member has one), no identifier.
        var maskedComment = comments.Single(c => c.Text == "from the member");
        Assert.Equal(masks, maskedComment.UserId == Guid.Empty && maskedComment.User.Id == Guid.Empty);
        Assert.Equal(!masks, maskedComment.User.HasAvatar);
        Assert.Equal(masks, maskedComment.User.Identifier.Length == 0);
        Assert.Equal(masks, activity[log.Member].UserId == Guid.Empty && activity[log.Member].User.Id == Guid.Empty);
    }

    /// <summary>
    /// "Proposed by …" of an open grade proposal: the resting tablet names a consenting member and the boulder's own
    /// creator, and shows anyone else as "A member" with no id; every other viewer sees the name.
    /// </summary>
    public static async Task ProposalBylinesAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var cora = await ConsentingMemberAsync(h, "cora@test");
        Guid byCora, byCreator;
        await using (var db = h.CreateContext())
        {
            Guid Boulder(string name)
            {
                var boulder = new Boulder { WallId = s.WallId, Name = name, CreatedByUserId = h.Owner.Id };
                db.Boulders.Add(boulder);
                return boulder.Id;
            }

            (byCora, byCreator) = (Boulder("Cora's pick"), Boulder("Owner's own"));
            db.GradeProposals.Add(new GradeProposal { BoulderId = s.LiveId, ProposedByUserId = s.Member.Id, ProposedGrade = "6B" });
            db.GradeProposals.Add(new GradeProposal { BoulderId = byCora, ProposedByUserId = cora.Id, ProposedGrade = "6C" });
            db.GradeProposals.Add(new GradeProposal { BoulderId = byCreator, ProposedByUserId = h.Owner.Id, ProposedGrade = "7A" });
            await db.SaveChangesAsync();
        }

        var boulders = s.Boulders(viewer);
        var masks = viewer == DraftViewer.KioskAnonymous;
        var member = (await boulders.GetActiveProposalAsync(s.LiveId))!;
        Assert.Equal(masks ? KioskBylines.PlaceholderName : s.Member.Name, member.ProposedBy.Name);
        Assert.Equal(masks ? Guid.Empty : s.Member.Id, member.ProposedByUserId);
        Assert.Equal(masks ? Guid.Empty : s.Member.Id, member.ProposedBy.Id);
        Assert.Equal(cora.Name, (await boulders.GetActiveProposalAsync(byCora))!.ProposedBy.Name);
        Assert.Equal(h.Owner.Name, (await boulders.GetActiveProposalAsync(byCreator))!.ProposedBy.Name);
    }

    private static async Task<(Guid Cora, Guid Member, Guid Created, Guid Erased)> SeedAsync(
        WallTestHarness h, DraftExposureScenario s, User cora, Guid erased)
    {
        await using var db = h.CreateContext();
        var member = await db.Users.SingleAsync(u => u.Id == s.Member.Id);
        member.AvatarImage = [1, 2, 3];
        member.AvatarContentType = "image/png";
        db.BoulderComments.Add(new BoulderComment { BoulderId = s.LiveId, UserId = cora.Id, Text = "from cora" });
        db.BoulderComments.Add(new BoulderComment { BoulderId = s.LiveId, UserId = s.Member.Id, Text = "from the member" });
        db.BoulderComments.Add(new BoulderComment { BoulderId = s.LiveId, UserId = erased, Text = "from the erased" });
        Guid Log(Guid userId, ActivityType type)
        {
            var entry = new ActivityLogEntry { WallId = s.WallId, BoulderId = s.LiveId, UserId = userId, Type = type };
            db.ActivityLog.Add(entry);
            return entry.Id;
        }

        var ids = (Log(cora.Id, ActivityType.AttemptLogged), Log(s.Member.Id, ActivityType.AttemptLogged),
            Log(s.Member.Id, ActivityType.BoulderCreated), Log(erased, ActivityType.AttemptLogged));
        await db.SaveChangesAsync();
        return ids;
    }

    internal static async Task<User> ConsentingMemberAsync(WallTestHarness h, string identifier)
    {
        var user = await h.AddMemberAsync(identifier, WallRole.Member);
        await using var db = h.CreateContext();
        await db.WallMembers.Where(m => m.WallId == h.WallId && m.UserId == user.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.KioskConsentedAt, DateTimeOffset.UtcNow));
        return user;
    }

    internal static async Task<Guid> ErasedUserAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = "deleted__" + Guid.NewGuid().ToString("N"), DisplayName = "Ghost", DeletedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
