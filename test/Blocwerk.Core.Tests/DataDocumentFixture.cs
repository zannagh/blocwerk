// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Assets.Documents;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The data documents over a seeded wall (SQLite, or Postgres when given): a published boulder and a draft, a setter
/// (moderator), a plain member, a share token, a gallery image, and a second wall. The test sets who is asking
/// (<see cref="Caller"/>, <see cref="Principal"/>, <see cref="BindKiosk"/>) and calls the service or the registered routes.
/// </summary>
internal sealed class DataDocumentFixture : IDisposable
{
    public const string ShareToken = "docs-share-token";

    private WebApplication? app;

    public DataDocumentFixture(PostgresTestDatabase? postgres = null)
    {
        Harness = new WallTestHarness(postgres);
        Feed = new WallFeed(() => Harness.RootContextFactory.CreateDbContext(), NullLogger<WallFeed>.Instance);
        Kiosk = Substitute.For<IKioskContext>();
        KioskKeys = Substitute.For<IKioskKeyValidator>();
        KioskKeys.IsKeyValidAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        CurrentUser = Substitute.For<ICurrentUserService>();
        CurrentUser.GetCurrentUserAsync().Returns(_ => Caller is { } user
            ? Task.FromResult(user)
            : Task.FromException<User>(new UnauthorizedAccessException()));
    }

    public WallTestHarness Harness { get; }

    /// <summary>Gets or sets the feed the service reads its marks from (a test may wrap it).</summary>
    public IWallFeed Feed { get; set; }

    public DataDocumentStore Store { get; } = new();

    public IKioskContext Kiosk { get; }

    public IKioskKeyValidator KioskKeys { get; }

    public ICurrentUserService CurrentUser { get; }

    public Guid KioskKeyId { get; } = Guid.NewGuid();

    public Guid WallId => Harness.WallId;

    public Guid OtherWallId { get; private set; }

    public User Setter { get; private set; } = null!;

    public User Member { get; private set; } = null!;

    public Guid PublishedId { get; private set; }

    public Guid DraftId { get; private set; }

    /// <summary>Gets or sets who the session resolves to; null when nobody.</summary>
    public User? Caller { get; set; }

    /// <summary>Gets or sets the request's principal; by default authenticated exactly when <see cref="Caller"/> is set.</summary>
    public ClaimsPrincipal? Principal { get; set; }

    public async Task SeedAsync()
    {
        var holds = await Harness.SeedWallAsync(holdCount: 3);
        Setter = await Harness.AddMemberAsync("setter@test", WallRole.Moderator);
        Member = await Harness.AddMemberAsync("member@test", WallRole.Member);
        await using var db = Harness.CreateContext();
        var wall = await db.Walls.FindAsync(WallId);
        wall!.ShareToken = ShareToken;
        var published = Boulder("Published", false, holds[0].Id, holds[1].Id);
        var draft = Boulder("Secret draft", true, holds[2].Id);
        db.Boulders.AddRange(published, draft);
        db.BoulderSetters.Add(new BoulderSetter { BoulderId = published.Id, UserId = Setter.Id });
        db.BoulderSetters.Add(new BoulderSetter { BoulderId = draft.Id, UserId = Setter.Id });
        db.Attempts.Add(new Attempt { BoulderId = published.Id, UserId = Member.Id, Type = AttemptType.Send });
        db.BoulderRatings.Add(new BoulderRating { BoulderId = published.Id, UserId = Member.Id, Stars = 4 });
        db.WallImages.Add(new WallImage { WallId = WallId, StoragePath = "g1.jpg", ContentType = "image/jpeg", SizeBytes = 3 });
        var other = new Wall { Name = "Other", OwnerId = Harness.Owner.Id, ShareToken = "other-token" };
        db.Walls.Add(other);
        db.WallMembers.Add(new WallMember { WallId = other.Id, UserId = Harness.Owner.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        (PublishedId, DraftId, OtherWallId) = (published.Id, draft.Id, other.Id);
        Caller = Harness.Owner;
    }

    public DataDocumentService Service(DataDocumentStore? store = null) => new(
        Harness.RootContextFactory,
        new DataDocumentBuilder(Harness.RootContextFactory, Harness.WallImageStorage),
        store ?? Store,
        Feed);

    public FeedSession Session() => new(
        new FeedCaller(Principal?.Identity?.IsAuthenticated ?? Caller is not null, Principal?.IsApiKeyPrincipal() == true),
        CurrentUser,
        Kiosk,
        KioskKeys);

    public Task<DocumentAccess> AccessAsync(string? token = null, Guid? wallId = null) =>
        Service().AccessAsync(wallId ?? WallId, token, Session(), default);

    /// <summary>The caller's index through the service, as the route would build it.</summary>
    public async Task<DataDocumentIndex> IndexAsync(string? token = null)
    {
        var access = await AccessAsync(token);
        return (await Service().IndexAsync(WallId, access.Audience!.Value, default))!;
    }

    /// <summary>The caller's document of <paramref name="kind"/>, read through the service and parsed.</summary>
    public async Task<T> DocumentAsync<T>(string kind, string? token = null)
    {
        var index = await IndexAsync(token);
        var access = await AccessAsync(token);
        var stored = await Service().GetAsync(WallId, access.Audience!.Value, kind, Sha(index, kind), default);
        return JsonSerializer.Deserialize<T>(stored!.Json, DataDocumentSchema.Options)!;
    }

    public static string Sha(DataDocumentIndex index, string kind) => index.Docs.Single(d => d.Kind == kind).Sha;

    /// <summary>Writes a feed row of the wall straight into the table and returns it, positioned.</summary>
    public async Task<WallChange> AddFeedRowAsync(string kind, FeedAudience audience = FeedAudience.Wall)
    {
        await using var db = Harness.CreateContext();
        var row = Row(kind, audience);
        db.WallChanges.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    /// <summary>Adds a published boulder and its <c>boulder.changed</c> row in one save, as the interceptor would.</summary>
    public async Task<(Guid BoulderId, WallChange Row)> AddBoulderAsync(string name, BlocwerkDbContext? into = null)
    {
        var db = into ?? Harness.CreateContext();
        var boulder = new Boulder { WallId = WallId, Name = name, CreatedByUserId = Setter.Id };
        var row = Row(FeedKinds.BoulderChanged, FeedAudience.Wall);
        db.Boulders.Add(boulder);
        db.WallChanges.Add(row);
        await db.SaveChangesAsync();
        if (into is null)
        {
            await db.DisposeAsync();
        }

        return (boulder.Id, row);
    }

    public void BindKiosk(Guid wallId)
    {
        Kiosk.IsKiosk.Returns(true);
        Kiosk.KioskWallId.Returns(wallId);
        Kiosk.KioskApiKeyId.Returns(KioskKeyId);
    }

    public Task<DefaultHttpContext> IndexHttpAsync(string? token = null, Guid? wallId = null, Action<HttpContext>? configure = null) =>
        GetAsync(DocumentEndpoints.IndexRoute, wallId ?? WallId, null, null, token, configure);

    public Task<DefaultHttpContext> DocumentHttpAsync(
        string kind, string sha, string? token = null, Guid? wallId = null, Action<HttpContext>? configure = null) =>
        GetAsync(DocumentEndpoints.Route, wallId ?? WallId, kind, sha, token, configure);

    public void Dispose()
    {
        (app as IDisposable)?.Dispose();
        Harness.Dispose();
    }

    private WallChange Row(string kind, FeedAudience audience) => new()
    {
        WallId = WallId, Kind = kind, EntityId = Guid.NewGuid(), Audience = audience, SchemaVersion = FeedSchema.PayloadVersion,
    };

    private Boulder Boulder(string name, bool draft, params Guid[] holdIds)
    {
        var boulder = new Boulder { WallId = WallId, Name = name, Grade = "6a", CreatedByUserId = Setter.Id, IsDraft = draft };
        foreach (var holdId in holdIds)
        {
            boulder.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId, Type = HoldType.Normal });
        }

        return boulder;
    }

    private async Task<DefaultHttpContext> GetAsync(
        string route, Guid wallId, string? kind, string? sha, string? token, Action<HttpContext>? configure)
    {
        app ??= BuildApp();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == route);
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.User = Principal ?? (Caller is null ? new ClaimsPrincipal(new ClaimsIdentity()) : FeedRouteFixture.SignedIn());
        http.Request.Method = HttpMethods.Get;
        http.Request.RouteValues["wallId"] = wallId.ToString();
        if (kind is not null)
        {
            http.Request.RouteValues["kind"] = kind;
            http.Request.RouteValues["sha"] = sha;
        }

        if (token is not null)
        {
            http.Request.QueryString = new QueryString("?token=" + Uri.EscapeDataString(token));
        }

        configure?.Invoke(http);
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        return http;
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Harness.RootContextFactory);
        builder.Services.AddSingleton(CurrentUser);
        builder.Services.AddSingleton(Kiosk);
        builder.Services.AddSingleton(KioskKeys);
        builder.Services.AddSingleton(_ => Service());
        builder.Services.AddSingleton(_ => new View3DDocumentService(
            Harness.DbContextFactory, CurrentUser, Substitute.For<IWall3DViewService>(), new View3DDocumentStore(), TimeProvider.System,
            NullLogger<View3DDocumentService>.Instance, Kiosk, KioskKeys));
        var built = builder.Build();
        built.MapWallDocuments();
        return built;
    }
}
