// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The feed routes in-process over a seeded wall (SQLite, or Postgres when given) and a real, started hub. The test sets
/// who is asking (<see cref="Caller"/>, <see cref="Principal"/>, <see cref="BindKiosk"/>), writes rows straight into
/// <c>WallChanges</c>, and calls the registered endpoints.
/// </summary>
internal sealed class FeedRouteFixture : IAsyncDisposable
{
    public const string ShareToken = "feed-share-token";

    private WebApplication? app;

    public FeedRouteFixture(PostgresTestDatabase? postgres = null, FeedStreamOptions? options = null)
    {
        Harness = new WallTestHarness(postgres);
        var connectionString = Harness.DbContextFactory.ConnectionString;
        Feed = new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance);
        Hub = new WallFeedHub(Feed, Signal, () => TestDb.Create(connectionString), NullLogger<WallFeedHub>.Instance);
        CatchUp = new FeedCatchUp(Feed, () => TestDb.Create(connectionString));
        Options = options ?? new FeedStreamOptions { Lifetime = TimeSpan.FromSeconds(30) };
        Limits = new FeedStreamLimits(Options);
        Kiosk = Substitute.For<IKioskContext>();
        KioskKeys = Substitute.For<IKioskKeyValidator>();
        KioskKeys.IsKeyValidAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        CurrentUser = Substitute.For<ICurrentUserService>();
        CurrentUser.GetCurrentUserAsync().Returns(_ => Caller is { } user
            ? Task.FromResult(user)
            : Task.FromException<User>(new UnauthorizedAccessException()));
    }

    public WallTestHarness Harness { get; }

    public InProcessFeedSignal Signal { get; } = new();

    public WallFeed Feed { get; }

    public WallFeedHub Hub { get; }

    public FeedCatchUp CatchUp { get; }

    public FeedStreamOptions Options { get; }

    public FeedStreamLimits Limits { get; }

    public IKioskContext Kiosk { get; }

    /// <summary>Gets the kiosk key check; every key is live until a test says otherwise.</summary>
    public IKioskKeyValidator KioskKeys { get; }

    /// <summary>Gets the app's shutdown, as the stream host sees it.</summary>
    public CancellationTokenSource Stopping { get; } = new();

    public ICurrentUserService CurrentUser { get; }

    public Guid WallId => Harness.WallId;

    public Guid OtherWallId { get; private set; }

    /// <summary>Who the session resolves to; null when nobody (or nobody who may sign in).</summary>
    public User? Caller { get; set; }

    /// <summary>The request's principal; by default authenticated exactly when <see cref="Caller"/> is set.</summary>
    public ClaimsPrincipal? Principal { get; set; }

    public async Task StartAsync()
    {
        await Harness.SeedWallAsync(holdCount: 0);
        await using var db = Harness.CreateContext();
        var wall = await db.Walls.FindAsync(WallId);
        wall!.ShareToken = ShareToken;
        var stranger = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
        var other = new Wall { Name = "Other", OwnerId = stranger.Id, ShareToken = "other-token" };
        db.Users.Add(stranger);
        db.Walls.Add(other);
        db.WallMembers.Add(new WallMember { WallId = other.Id, UserId = stranger.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        OtherWallId = other.Id;
        Caller = Harness.Owner;
        await Hub.StartAsync(CancellationToken.None);
    }

    /// <summary>A plain member of the wall, added by <see cref="SeedAudiencesAsync"/>.</summary>
    public User Member { get; private set; } = null!;

    /// <summary>One row per audience: Wall, Members, Setters, the owner's and the member's own, and one of another wall.</summary>
    public async Task SeedAudiencesAsync()
    {
        Member = await Harness.AddMemberAsync("member@test", WallRole.Member);
        await CommitAsync(
            false,
            Row("wall"),
            Row("members", FeedAudience.Members),
            Row("setters", FeedAudience.Setters),
            Row("owner.mine", FeedAudience.User, Harness.Owner.Id),
            Row("member.mine", FeedAudience.User, Member.Id),
            Row("other.wall", wallId: OtherWallId));
    }

    /// <summary>A cursor before every row of the current epoch.</summary>
    public async Task<string> BeginningAsync() => new FeedCursor((await Feed.CurrentAsync()).Epoch, FeedPosition.Start).ToString();

    public void BindKiosk(Guid wallId)
    {
        Kiosk.IsKiosk.Returns(true);
        Kiosk.KioskWallId.Returns(wallId);
        Kiosk.KioskApiKeyId.Returns(KioskKeyId);
    }

    /// <summary>Gets the key id a bound kiosk carries.</summary>
    public Guid KioskKeyId { get; } = Guid.NewGuid();

    public static ClaimsPrincipal SignedIn() => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "x")], "Cookies"));

    public static ClaimsPrincipal ApiKey() => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "key")], ApiKeyAuthenticationHandler.SchemeName));

    public WallChange Row(
        string kind, FeedAudience audience = FeedAudience.Wall, Guid? user = null, Guid? wallId = null, Guid? actor = null,
        Guid? entityId = null) => new()
    {
        WallId = wallId ?? WallId,
        Kind = kind,
        EntityId = entityId ?? Guid.NewGuid(),
        Audience = audience,
        AudienceUserId = user,
        ActorUserId = actor,
    };

    /// <summary>Commits the rows; wakes the hub for <see cref="WallId"/> when asked.</summary>
    public async Task<IReadOnlyList<WallChange>> CommitAsync(bool wake, params WallChange[] rows)
    {
        await using var db = Harness.CreateContext();
        db.WallChanges.AddRange(rows);
        await db.SaveChangesAsync();
        if (wake)
        {
            Signal.Wake(new FeedWake(WallId, rows[^1].TxId));
        }

        return rows;
    }

    /// <summary>Calls a feed route in-process; <paramref name="body"/> receives the response (a buffer by default).</summary>
    public async Task<DefaultHttpContext> GetAsync(
        string route, Guid? wallId = null, string? query = null, Stream? body = null, Action<HttpContext>? configure = null,
        CancellationToken ct = default)
    {
        app ??= BuildApp();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == route);
        var http = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = ct };
        http.User = Principal ?? (Caller is null ? new ClaimsPrincipal(new ClaimsIdentity()) : SignedIn());
        http.Request.Method = HttpMethods.Get;
        if (wallId is { } id)
        {
            http.Request.RouteValues["wallId"] = id.ToString();
        }

        if (query is not null)
        {
            http.Request.QueryString = new QueryString(query);
        }

        configure?.Invoke(http);
        http.Response.Body = body ?? new MemoryStream();
        await endpoint.RequestDelegate!(http);
        return http;
    }

    public Task<DefaultHttpContext> WallChangesAsync(string? query = null, Guid? wallId = null) =>
        GetAsync(FeedEndpoints.WallChangesRoute, wallId ?? WallId, query);

    public Task<DefaultHttpContext> SelfChangesAsync(string? query = null) => GetAsync(FeedEndpoints.SelfChangesRoute, query: query);

    public static T Json<T>(HttpContext http) =>
        JsonSerializer.Deserialize<T>(((MemoryStream)http.Response.Body).ToArray(), FeedJson.Options)!;

    public static FeedChanges Changes(HttpContext http)
    {
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        return Json<FeedChanges>(http);
    }

    /// <summary>Asserts the refusal's status and its body's code.</summary>
    public static void AssertRefused(HttpContext http, int status, string error)
    {
        Assert.Equal(status, http.Response.StatusCode);
        Assert.Equal(error, Json<FeedRefusal>(http).Error);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }

        await Hub.StopAsync(CancellationToken.None);
        Hub.Dispose();
        Stopping.Dispose();
        Harness.Dispose();
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<RootDbContextFactory>(Harness.RootContextFactory);
        builder.Services.AddSingleton(CurrentUser);
        builder.Services.AddSingleton(Kiosk);
        builder.Services.AddSingleton(KioskKeys);
        builder.Services.AddSingleton(CatchUp);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(Stopping.Token);
        builder.Services.AddSingleton(new FeedStreamHost(Hub, Feed, CatchUp, Options, Limits, lifetime));
        var built = builder.Build();
        built.MapFeed();
        return built;
    }
}
