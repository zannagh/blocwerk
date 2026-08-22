using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="ApiKeySurface"/>'s prefixes are open by default: ANY endpoint mounted under
/// /api/walls or /api/v1 authenticates an API key, and only the endpoint's own authorization
/// metadata narrows that down to the wall the key was issued for. An endpoint added there without
/// it is a cross-wall read waiting to happen — this test fails the build instead.
/// </summary>
/// <remarks>
/// The real host cannot be started here: it migrates Postgres on startup, so a WebApplicationFactory
/// would need a live database. The route table is therefore rebuilt from the very registration
/// calls Program.Main makes for these prefixes — <c>MapControllers</c>, <c>MapWallPhotos</c> and
/// <c>MapWallGalleryImages</c>. Anything mapped under a covered prefix must be registered through
/// one of those, which is why the wall photo routes were moved out of Program.cs into
/// <see cref="WallPhotoEndpoints"/>.
/// </remarks>
public class ApiSurfaceAuthorizationTests
{
    [Fact]
    public void EveryEndpointUnderAnApiKeyPrefix_DeclaresItsOwnAuthorization()
    {
        var unguarded = CoveredEndpoints()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                        && e.Metadata.GetMetadata<DeniesApiKeyPrincipals>() is null)
            .Select(e => $"{e.RoutePattern.RawText} ({e.DisplayName})")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var message = "These endpoints sit under an ApiKeySurface prefix but declare no "
            + "authorization, so a leaked API key authenticates them as the key's owner with full "
            + "membership visibility. Add [Authorize(...)]/RequireAuthorization, or reject "
            + "API-key principals the way WallPhotoEndpoints does with DenyApiKeyPrincipals():"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unguarded);

        Assert.True(unguarded.Count == 0, message);
    }

    /// <summary>
    /// <see cref="ApiKeySurface.HasExplicitAuthorization"/> only proves an endpoint declares SOME
    /// authorization — it cannot prove the endpoint compares the route's wallId against the wall
    /// the key was issued for. That comparison lives in <c>WallScopedApiController.GuardWall</c>,
    /// so every controller action under /api/walls has to inherit it. A plain
    /// <c>ControllerBase</c> action with <c>[Authorize(Policy = WallApiKey)]</c> would satisfy the
    /// runtime check and the metadata test above while reading any wall it likes — this test is
    /// what stops it.
    /// </summary>
    [Fact]
    public void EveryWallScopedControllerAction_DerivesFromWallScopedApiController()
    {
        var offenders = CoveredEndpoints()
            .Where(e => ToPath(e.RoutePattern.RawText)
                .StartsWithSegments(ApiKeySurface.WallApiPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Metadata.GetMetadata<DeniesApiKeyPrincipals>() is null)
            .Select(e => new
            {
                Route = e.RoutePattern.RawText,
                Action = e.Metadata.GetMetadata<ControllerActionDescriptor>(),
            })
            .Where(x => x.Action is null
                        || !typeof(WallScopedApiController).IsAssignableFrom(x.Action.ControllerTypeInfo.AsType()))
            .Select(x => $"{x.Route} ({x.Action?.ControllerTypeInfo.FullName ?? "not a controller action"})")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var message = "These endpoints sit under " + ApiKeySurface.WallApiPrefix + " but are not "
            + "declared on a controller deriving from WallScopedApiController, so nothing forces "
            + "them to call GuardWall(wallId) — a key issued for one wall could act on another. "
            + "Derive the controller from WallScopedApiController and guard the route's wallId, or "
            + "reject API-key principals the way WallPhotoEndpoints does with DenyApiKeyPrincipals():"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders);

        Assert.True(offenders.Count == 0, message);
    }

    [Fact]
    public void TheRouteTableActuallyContainsTheCoveredEndpoints()
    {
        // Without this the test above passes vacuously the day a registration call is renamed.
        var routes = CoveredEndpoints()
            .Select(e => ToPath(e.RoutePattern.RawText).Value ?? string.Empty)
            .ToList();

        Assert.Contains("/api/walls/{wallId:guid}/photo", routes);
        Assert.Contains("/api/walls/{wallId:guid}/staged-photo", routes);
        Assert.Contains("/api/walls/{wallId:guid}/temperature", routes);
        Assert.Contains("/api/v1/me/sessions", routes);

        // The browser gallery route lives at /walls/…, outside the prefixes, so it must NOT show
        // up here — if it ever moved under /api/walls this assertion would say so.
        Assert.DoesNotContain(routes, r => r.Contains("/gallery/", StringComparison.Ordinal));
    }

    /// <summary>Every registered endpoint whose route falls under an API-key prefix.</summary>
    private static IReadOnlyList<RouteEndpoint> CoveredEndpoints()
    {
        IEndpointRouteBuilder routes = BuildRouteTable();

        return routes.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => ApiKeySurface.Covers(ToPath(endpoint.RoutePattern.RawText)))
            .ToList();
    }

    private static WebApplication BuildRouteTable()
    {
        var builder = WebApplication.CreateBuilder();

        // Only what MapControllers needs to materialise its action endpoints. Nothing is
        // constructed: controller instances and their services are resolved per request.
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(WallTemperatureController).Assembly);
        builder.Services.AddAuthorization();

        // Minimal APIs infer "service vs body" from what the container knows about, so the
        // services their handlers take have to be registered for the routes to materialise. They
        // are never resolved here — no request is executed — so substitutes are enough.
        builder.Services.AddSingleton(Substitute.For<IWallService>());
        builder.Services.AddSingleton(Substitute.For<IWallImageService>());
        builder.Services.AddSingleton(Substitute.For<IWallImageStorage>());
        builder.Services.AddSingleton(Substitute.For<ICurrentUserService>());
        builder.Services.AddSingleton(Substitute.For<IDbContextFactory<BlocwerkDbContext>>());

        var app = builder.Build();
        app.MapControllers();
        app.MapWallPhotos();
        app.MapWallGalleryImages();
        return app;
    }

    private static PathString ToPath(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return PathString.Empty;
        }

        return new PathString(rawText.StartsWith('/') ? rawText : "/" + rawText);
    }
}
