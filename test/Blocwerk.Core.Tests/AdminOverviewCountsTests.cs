using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The admin overview counts people, not rows.
/// </summary>
public class AdminOverviewCountsTests
{
    [Fact]
    public async Task RegisteredUsersExcludesTombstonesAndTheGhostRow()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);

        var beforeDeletion = await CountAsync(harness);
        Assert.True(await fixture.Service.DeleteAsync(fixture.LeaverId));
        var afterDeletion = await CountAsync(harness);

        // Somebody left, so the number goes down by exactly one — it used to stay put, because the
        // tombstone is still a row.
        Assert.Equal(beforeDeletion - 1, afterDeletion);

        // And the seeded system row is never one of the people counted.
        await using var db = harness.CreateContext();
        var rows = await db.Users.CountAsync();
        Assert.True(await db.Users.AnyAsync(u => u.Id == GhostUser.Id));
        Assert.True(afterDeletion < rows);
    }

    private static async Task<int> CountAsync(WallTestHarness harness)
    {
        var admin = Substitute.For<ICurrentUserService>();
        admin.GetCurrentUserAsync().Returns(_ => Task.FromResult(new User
        {
            Identifier = "admin__1",
            DisplayName = "Admin",
            Role = IdentityRole.Admin,
        }));

        var service = new AdminDashboardService(harness.DbContextFactory, admin);
        var overview = await service.GetOverviewAsync();
        return overview.TotalUsers;
    }
}
