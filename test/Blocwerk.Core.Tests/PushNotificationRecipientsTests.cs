using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Exercises the push-notification RESOLUTION/filter layer directly: who ends up a send target for a
/// given event, given wall membership, setters, opt-out masks and existing subscriptions. These are
/// the pure resolver methods on <see cref="PushNotificationService"/>; the actual WebPush HTTP send
/// and its 410-pruning are integration-level and deliberately NOT asserted here.
/// </summary>
/// <remarks>
/// Own connection per context over a named shared-cache SQLite database, mirroring
/// <see cref="WallTestHarness"/> — the production <see cref="IDbContextFactory{TContext}"/> hands out
/// independent connections, and sharing one would race EF's user-function registration (SQLITE_BUSY).
/// </remarks>
public sealed class PushNotificationRecipientsTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;

    public PushNotificationRecipientsTests()
    {
        var connectionString = TestDbContextFactory.IsolatedDatabase();
        connection = new SqliteConnection(connectionString);
        connection.Open();
        factory = new TestDbContextFactory(connectionString);

        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    [Fact]
    public async Task ResolveSendTargets_excludes_a_user_who_opted_out_of_the_type()
    {
        await using var db = Context();
        var optedOut = AddUser(db, disabled: NotificationType.CommentOnYourBoulder);
        var enabled = AddUser(db);
        await db.SaveChangesAsync();

        var targets = await PushNotificationService.ResolveSendTargetsAsync(
            db,
            new[] { optedOut.Id, enabled.Id },
            NotificationType.CommentOnYourBoulder);

        // Only the enabled user's subscription is a target; the opted-out bit removes the other.
        var target = Assert.Single(targets);
        Assert.Equal(EndpointFor(enabled.Id), target.Endpoint);
    }

    [Fact]
    public async Task WallMemberIds_excludes_the_actor_even_though_they_are_a_member()
    {
        await using var db = Context();
        var actor = AddUser(db);
        var other = AddUser(db);
        var wall = AddWall(db, actor.Id);
        AddMember(db, wall.Id, actor.Id, WallRole.Admin);
        AddMember(db, wall.Id, other.Id, WallRole.Member);
        await db.SaveChangesAsync();

        var recipients = await PushNotificationService.WallMemberIdsAsync(db, wall.Id, actor.Id);

        Assert.Equal(new[] { other.Id }, recipients);
        Assert.DoesNotContain(actor.Id, recipients);
    }

    [Fact]
    public async Task ResolveSendTargets_yields_nothing_for_a_member_with_no_subscription()
    {
        await using var db = Context();
        var noSubscription = AddUser(db, subscribed: false);
        await db.SaveChangesAsync();

        var targets = await PushNotificationService.ResolveSendTargetsAsync(
            db,
            new[] { noSubscription.Id },
            NotificationType.SessionStarted);

        Assert.Empty(targets);
    }

    [Fact]
    public async Task BoulderTargetRecipients_resolve_to_setters_plus_creator_not_the_whole_wall()
    {
        await using var db = Context();
        var creator = AddUser(db);
        var setter = AddUser(db);
        var actor = AddUser(db);
        var bystander = AddUser(db);

        var wall = AddWall(db, creator.Id);
        AddMember(db, wall.Id, creator.Id, WallRole.Admin);
        AddMember(db, wall.Id, setter.Id, WallRole.Member);
        AddMember(db, wall.Id, actor.Id, WallRole.Member);
        AddMember(db, wall.Id, bystander.Id, WallRole.Member);

        var boulder = new Boulder { WallId = wall.Id, Name = "Test Problem", CreatedByUserId = creator.Id };
        db.Boulders.Add(boulder);
        db.BoulderSetters.Add(new BoulderSetter { BoulderId = boulder.Id, UserId = setter.Id });

        // The actor is ALSO a co-setter, to prove the actor is dropped even from their own targeted set.
        db.BoulderSetters.Add(new BoulderSetter { BoulderId = boulder.Id, UserId = actor.Id });
        await db.SaveChangesAsync();

        var recipients = await PushNotificationService.BoulderTargetRecipientsAsync(db, boulder.Id, actor.Id);

        Assert.Contains(creator.Id, recipients);
        Assert.Contains(setter.Id, recipients);
        Assert.DoesNotContain(actor.Id, recipients);
        Assert.DoesNotContain(bystander.Id, recipients);
    }

    private BlocwerkDbContext Context()
    {
        var db = factory.CreateDbContext();
        db.CurrentUserId = Guid.Empty;
        return db;
    }

    private static string EndpointFor(Guid userId) => $"https://push.example/{userId:N}";

    private static User AddUser(
        BlocwerkDbContext db,
        NotificationType disabled = NotificationType.None,
        bool subscribed = true)
    {
        var user = new User
        {
            Identifier = Guid.NewGuid().ToString("N"),
            DisplayName = "Member",
            DisabledNotifications = disabled,
        };
        db.Users.Add(user);

        if (subscribed)
        {
            db.PushSubscriptions.Add(new PushSubscription
            {
                UserId = user.Id,
                Endpoint = EndpointFor(user.Id),
                P256dh = "p256dh",
                Auth = "auth",
            });
        }

        return user;
    }

    private static Wall AddWall(BlocwerkDbContext db, Guid ownerId)
    {
        var wall = new Wall
        {
            Name = "Test Wall",
            OwnerId = ownerId,
            Photo = [1],
            PhotoContentType = "image/jpeg",
        };
        db.Walls.Add(wall);
        return wall;
    }

    private static void AddMember(BlocwerkDbContext db, Guid wallId, Guid userId, WallRole role)
    {
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = userId, Role = role });
    }
}
