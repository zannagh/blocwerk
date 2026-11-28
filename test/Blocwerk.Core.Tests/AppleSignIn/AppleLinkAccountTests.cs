// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>What happens to a linked Apple ID when its account is merged into another or removed.</summary>
public sealed class AppleLinkAccountTests
{
    [Fact]
    public async Task AMerge_MovesTheSourcesAppleId_WhenTheTargetHasNone()
    {
        using var bed = AppleSignInTestBed.Create();
        var source = await bed.AddUserAsync("source");
        var target = await bed.AddUserAsync("target");
        await bed.LinkAsync(source.Id, "apple-sub-1");

        await Merger(bed).MergeUsersAsync(source.Id, target.Id);

        Assert.True(await bed.Service.IsLinkedAsync(target.Id));
        Assert.Equal(AppleSignInOutcome.SignedIn, (await bed.SignInAsync("apple-sub-1")).Outcome);
    }

    [Fact]
    public async Task AMerge_KeepsTheTargetsAppleId_AndDropsTheSources()
    {
        using var bed = AppleSignInTestBed.Create();
        var source = await bed.AddUserAsync("source");
        var target = await bed.AddUserAsync("target");
        await bed.LinkAsync(source.Id, "apple-sub-1");
        await bed.LinkAsync(target.Id, "apple-sub-2");

        await Merger(bed).MergeUsersAsync(source.Id, target.Id);

        await using var db = bed.Harness.CreateContext();
        var link = await db.AppleLinks.SingleAsync();
        Assert.Equal(target.Id, link.UserId);
        Assert.Equal("apple-sub-2", link.Subject);
    }

    [Fact]
    public async Task RemovingTheUserRow_RemovesItsAppleId()
    {
        using var bed = AppleSignInTestBed.Create();
        var user = await bed.AddUserAsync("climber");
        await bed.LinkAsync(user.Id, "apple-sub-1");

        await using var db = bed.Harness.CreateContext();
        db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id));
        await db.SaveChangesAsync();

        Assert.Empty(await db.AppleLinks.ToListAsync());
    }

    [Fact]
    public async Task ErasingTheAccount_RemovesItsAppleId()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);
        await using (var db = harness.CreateContext())
        {
            db.AppleLinks.Add(new AppleLink { UserId = fixture.LeaverId, Subject = "apple-sub-1", LinkedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.True(await fixture.Service.DeleteAsync(fixture.LeaverId));

        await using var check = harness.CreateContext();
        Assert.Empty(await check.AppleLinks.ToListAsync());
    }

    private static AccountMergeService Merger(AppleSignInTestBed bed) =>
        new(bed.Harness.DbContextFactory, NullLogger<AccountMergeService>.Instance);
}
