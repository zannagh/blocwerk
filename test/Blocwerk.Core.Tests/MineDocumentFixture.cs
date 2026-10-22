// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Mine;
using Blocwerk.Core.Assets.Mine;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The reader's own marks (<c>/api/me/walls/{w}/mine</c>, PR 3.4) over <see cref="DataDocumentFixture"/>'s wall, with
/// marks of three people on it: the member (on every boulder kind, two favourites, and a tick on another wall), the
/// setter (their own tick and favourites, which must never reach anyone else), and a non-member.
/// </summary>
internal sealed class MineDocumentFixture : IDisposable
{
    private WebApplication? app;

    public MineDocumentFixture(PostgresTestDatabase? postgres = null)
    {
        Docs = new DataDocumentFixture(postgres);
    }

    public DataDocumentFixture Docs { get; }

    public Guid WallId => Docs.WallId;

    public User Member => Docs.Member;

    public User Setter => Docs.Setter;

    public User Outsider { get; private set; } = null!;

    public Guid OtherWallBoulderId { get; private set; }

    public async Task SeedAsync()
    {
        await Docs.SeedAsync();
        await using var db = Docs.Harness.CreateContext();
        Outsider = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(Outsider);
        var other = new Boulder { WallId = Docs.OtherWallId, Name = "Elsewhere", Grade = "5", CreatedByUserId = Docs.Harness.Owner.Id };
        db.Boulders.Add(other);
        OtherWallBoulderId = other.Id;
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        // The member already sent the published boulder (DataDocumentFixture); one more attempt there, and one per kind.
        db.Attempts.AddRange(
            new Attempt { BoulderId = Docs.PublishedId, UserId = Member.Id, Type = AttemptType.Attempt, Timestamp = at.AddDays(1) },
            new Attempt { BoulderId = Docs.DraftId, UserId = Member.Id, Type = AttemptType.Flash, Timestamp = at },
            new Attempt { BoulderId = Docs.ArchivedId, UserId = Member.Id, Type = AttemptType.Attempt, Timestamp = at },
            new Attempt { BoulderId = Docs.HistoricId, UserId = Member.Id, Type = AttemptType.Send, Timestamp = at },
            new Attempt { BoulderId = other.Id, UserId = Member.Id, Type = AttemptType.Send, Timestamp = at },
            new Attempt { BoulderId = Docs.PublishedId, UserId = Setter.Id, Type = AttemptType.Flash, Timestamp = at },
            new Attempt { BoulderId = Docs.PublishedId, UserId = Outsider.Id, Type = AttemptType.Send, Timestamp = at });
        db.BoulderFavorites.AddRange(
            new BoulderFavorite { BoulderId = Docs.PublishedId, UserId = Member.Id },
            new BoulderFavorite { BoulderId = Docs.DraftId, UserId = Member.Id },
            new BoulderFavorite { BoulderId = other.Id, UserId = Member.Id },
            new BoulderFavorite { BoulderId = Docs.HistoricId, UserId = Setter.Id },
            new BoulderFavorite { BoulderId = Docs.PublishedId, UserId = Outsider.Id });
        await db.SaveChangesAsync();
        Docs.Caller = Member;
    }

    public MineDocumentService Service() => new(Docs.Harness.RootContextFactory, Docs.Feed);

    public Task<MineAccess> AccessAsync(Guid? wallId = null) => Service().AccessAsync(wallId ?? WallId, Docs.Session(), default);

    /// <summary>The caller's marks through the service, as the route builds them.</summary>
    public async Task<MineDocument> MineAsync()
    {
        var access = await AccessAsync();
        return await Service().BuildAsync(WallId, access.UserId!.Value, access.Audience!.Value, default);
    }

    /// <summary>Calls the registered route as the fixture's caller.</summary>
    public async Task<DefaultHttpContext> HttpAsync(Guid? wallId = null)
    {
        app ??= BuildApp();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == MineEndpoints.Route);
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.User = Docs.Principal ?? (Docs.Caller is null ? new ClaimsPrincipal(new ClaimsIdentity()) : FeedRouteFixture.SignedIn());
        http.Request.Method = HttpMethods.Get;
        http.Request.RouteValues["wallId"] = (wallId ?? WallId).ToString();
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        return http;
    }

    public static string Body(HttpContext http) => System.Text.Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

    public static MineDocument Parse(HttpContext http) => JsonSerializer.Deserialize<MineDocument>(Body(http), DataDocumentSchema.Options)!;

    public void Dispose()
    {
        (app as IDisposable)?.Dispose();
        Docs.Dispose();
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Docs.Harness.RootContextFactory);
        builder.Services.AddSingleton(Docs.CurrentUser);
        builder.Services.AddSingleton(Docs.Kiosk);
        builder.Services.AddSingleton(Docs.KioskKeys);
        builder.Services.AddSingleton(_ => Service());
        var built = builder.Build();
        built.MapMine();
        return built;
    }
}
