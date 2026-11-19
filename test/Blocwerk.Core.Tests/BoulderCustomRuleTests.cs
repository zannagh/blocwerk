using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>A boulder's free-text custom rule: normalised on write, set only by someone who may edit the boulder, and offline.</summary>
public class BoulderCustomRuleTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  no kickboard  ", "no kickboard")]
    public void Normalize_TrimsAndTurnsEmptyIntoNull(string? input, string? expected) =>
        Assert.Equal(expected, BoulderCustomRule.Normalize(input));

    [Fact]
    public void Normalize_AcceptsExactlyTheMaximumAndRefusesMore()
    {
        Assert.Equal(200, BoulderCustomRule.Normalize(new string('a', 200))!.Length);
        Assert.Throws<InvalidOperationException>(() => BoulderCustomRule.Normalize(new string('a', 201)));
    }

    [Fact]
    public async Task Create_StoresTheRuleTrimmed_AndAnEmptyOneAsNull()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);

        var withRule = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "A", null, [new BoulderHoldInput(holds[0].Id)], customRule: "  start matched ");
        var without = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)], customRule: "  ");

        await using var db = h.CreateContext();
        Assert.Equal("start matched", (await db.Boulders.FirstAsync(b => b.Id == withRule.Id)).CustomRule);
        Assert.Null((await db.Boulders.FirstAsync(b => b.Id == without.Id)).CustomRule);
    }

    [Fact]
    public async Task Revise_SetsKeepsAndClearsTheRule()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        List<BoulderHoldInput> same = [new BoulderHoldInput(holds[0].Id)];
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "A", null, same);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, customRule: "no heel hooks");
        Assert.Equal("no heel hooks", await StoredRuleAsync(h, boulder.Id));

        // null (an older offline snapshot, a remap) leaves it alone: a revision carries the rule on.
        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, name: "A2");
        Assert.Equal("no heel hooks", await StoredRuleAsync(h, boulder.Id));

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, customRule: string.Empty);
        Assert.Null(await StoredRuleAsync(h, boulder.Id));
    }

    [Fact]
    public async Task Revise_ByAMemberWhoMayNotEdit_CannotSetTheRule()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        List<BoulderHoldInput> same = [new BoulderHoldInput(holds[0].Id)];
        var creator = await h.AddMemberAsync("creator@test", WallRole.Member);
        var other = await h.AddMemberAsync("other@test", WallRole.Member);

        h.ActingUser = creator;
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "A", null, same, customRule: "mine");

        h.ActingUser = other;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.BoulderService.ReviseBoulderAsync(boulder.Id, same, customRule: "hijacked"));

        Assert.Equal("mine", await StoredRuleAsync(h, boulder.Id));
    }

    [Fact]
    public async Task Revise_ByASetterAndByAWallAdmin_MaySetTheRule()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        List<BoulderHoldInput> same = [new BoulderHoldInput(holds[0].Id)];
        var setter = await h.AddMemberAsync("setter@test", WallRole.Member);
        var admin = await h.AddMemberAsync("admin@test", WallRole.Admin);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "A", null, same, setterUserIds: [setter.Id]);

        h.ActingUser = setter;
        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, customRule: "by the setter");
        Assert.Equal("by the setter", await StoredRuleAsync(h, boulder.Id));

        h.ActingUser = admin;
        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, customRule: "by the admin");
        Assert.Equal("by the admin", await StoredRuleAsync(h, boulder.Id));
    }

    [Fact]
    public async Task TheOfflineDocument_CarriesTheRule()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        await h.BoulderService.CreateBoulderAsync(
            h.WallId, "A", null, [new BoulderHoldInput(holds[0].Id)], customRule: "no kickboard");

        await using var db = h.CreateContext();
        var built = await BouldersDocumentBuilder.BuildAsync(db, h.WallId, DocumentAudience.Members, default);

        Assert.Equal("no kickboard", Assert.Single(built!.Value.Document.Boulders).CustomRule);
    }

    private static async Task<string?> StoredRuleAsync(WallTestHarness h, Guid boulderId)
    {
        await using var db = h.CreateContext();
        return (await db.Boulders.FirstAsync(b => b.Id == boulderId)).CustomRule;
    }
}
