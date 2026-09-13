using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The per-panel <c>/photo</c> route's fallback for a panel that carries NO committed blob of its own.
/// The correct image is the one the panel is REPLACING: the latest committed panel at the SAME
/// <c>(Col,Row)</c> from an earlier generation (the immutable-generation model keeps the superseded
/// row's photo). This is what the big-wall update's overlap stepper needs for its "existing neighbour"
/// image — a staged generation-N+1 panel has no committed blob, so without this fallback that image
/// rendered as a black 404 box. Only the <c>(0,0)</c> origin with no prior committed panel falls back
/// to the legacy <see cref="Wall.Photo"/>; any other blob-less position with no prior panel still 404s,
/// and the exact <c>(Col,Row)</c> match means one grid position's photo is never served for another.
/// Drives the real registered endpoint against the SQLite harness; only the panel byte/tag service is
/// substituted (to say which panels have a committed blob), while the Wall.Photo tier goes through the
/// real <see cref="WallService"/>.
/// </summary>
public class WallPanelPhotoFallbackTests : IDisposable
{
    private const string PanelPhotoRoute = "/api/walls/{wallId:guid}/panels/{panelId:guid}/photo";

    private static readonly byte[] PriorPanelBytes = [9, 8, 7];

    private readonly WallTestHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task StagedPanel_FallsBackToPriorCommittedPanelAtSamePosition()
    {
        // A committed generation-0 panel at (1,0) and a staged generation-1 panel at (1,0) with no
        // committed blob. Requesting the staged panel's /photo must resolve to the committed panel's
        // bytes (the image being replaced), NOT 404.
        var (committedRightId, stagedRightId) = await SeedReplacedNeighbourAsync();

        var panelService = SubstituteWithCommittedPanel(committedRightId);
        var invoke = await RouteAsync(panelService);

        var http = Request(stagedRightId);
        await invoke(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(PriorPanelBytes, Body(http));
    }

    [Fact]
    public async Task OriginPanel_WithNoCommittedBlobOrPriorPanel_FallsBackToWallPhoto()
    {
        var (centrePanelId, _) = await SeedMigratedWallAsync();
        var invoke = await RouteAsync(SubstituteWithNoCommittedPanels());

        var http = Request(centrePanelId);
        await invoke(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        // SeedWallAsync stores Wall.Photo = [1,2,3]; with no ?w= the original bytes are served verbatim.
        Assert.Equal(new byte[] { 1, 2, 3 }, Body(http));
    }

    [Fact]
    public async Task NonOriginPanel_WithNoCommittedPhotoAnywhere_Still404s()
    {
        var (_, rightPanelId) = await SeedMigratedWallAsync();
        var invoke = await RouteAsync(SubstituteWithNoCommittedPanels());

        var http = Request(rightPanelId);
        await invoke(http);

        Assert.Equal(StatusCodes.Status404NotFound, http.Response.StatusCode);
    }

    /// <summary>
    /// Seeds a migrated single-image wall: Wall.Photo present, a centre (0,0) panel and a right (1,0)
    /// panel BOTH with a null committed photo blob and NO prior committed panel at either position.
    /// Returns their ids.
    /// </summary>
    private async Task<(Guid CentrePanelId, Guid RightPanelId)> SeedMigratedWallAsync()
    {
        await harness.SeedWallAsync(holdCount: 0);

        await using var db = harness.CreateContext();
        var centre = new WallPanel { WallId = harness.WallId, Col = 0, Row = 0, Photo = null, Generation = 0 };
        var right = new WallPanel { WallId = harness.WallId, Col = 1, Row = 0, Photo = null, Generation = 0 };
        db.WallPanels.AddRange(centre, right);
        await db.SaveChangesAsync();
        return (centre.Id, right.Id);
    }

    /// <summary>
    /// Seeds a wall mid-update at (1,0): a committed generation-0 panel (the image being replaced) and a
    /// staged generation-1 panel with no committed blob at the SAME position. Returns (committed, staged).
    /// </summary>
    private async Task<(Guid CommittedRightId, Guid StagedRightId)> SeedReplacedNeighbourAsync()
    {
        await harness.SeedWallAsync(holdCount: 0);

        await using var db = harness.CreateContext();
        var committed = new WallPanel
        {
            WallId = harness.WallId, Col = 1, Row = 0, Generation = 0,
            Photo = PriorPanelBytes, PhotoContentType = "image/jpeg",
        };
        var staged = new WallPanel
        {
            WallId = harness.WallId, Col = 1, Row = 0, Generation = 1,
            Photo = null, StagedPhoto = [4, 5, 6], StagedPhotoContentType = "image/jpeg",
        };
        db.WallPanels.AddRange(committed, staged);
        await db.SaveChangesAsync();
        return (committed.Id, staged.Id);
    }

    /// <summary>
    /// A panel service reporting NO committed blob for any panel — the state of a fully migrated wall's
    /// panels, so the endpoint takes its Wall.Photo fallback path.
    /// </summary>
    private IWallPanelService SubstituteWithNoCommittedPanels()
    {
        var panelService = Substitute.For<IWallPanelService>();
        panelService.GetPanelPhotoTagAsync(harness.WallId, Arg.Any<Guid>()).Returns((WallPhotoTag?)null);
        panelService.GetPanelPhotoAsync(harness.WallId, Arg.Any<Guid>()).Returns((WallPhoto?)null);
        return panelService;
    }

    /// <summary>
    /// A panel service reporting a committed blob ONLY for <paramref name="committedPanelId"/> — the
    /// prior-generation panel the fallback should resolve to — and none for any other (staged) panel.
    /// </summary>
    private IWallPanelService SubstituteWithCommittedPanel(Guid committedPanelId)
    {
        var panelService = Substitute.For<IWallPanelService>();
        panelService.GetPanelPhotoTagAsync(harness.WallId, Arg.Any<Guid>()).Returns((WallPhotoTag?)null);
        panelService.GetPanelPhotoAsync(harness.WallId, Arg.Any<Guid>()).Returns((WallPhoto?)null);
        panelService.GetPanelPhotoTagAsync(harness.WallId, committedPanelId)
            .Returns(new WallPhotoTag(PriorPanelBytes.Length, "image/jpeg", 1, false));
        panelService.GetPanelPhotoAsync(harness.WallId, committedPanelId)
            .Returns(new WallPhoto(PriorPanelBytes, "image/jpeg"));
        return panelService;
    }

    /// <summary>
    /// The real registered panel <c>/photo</c> endpoint as a callable delegate, backed by the given
    /// (substituted) panel byte/tag service. The Wall.Photo fallback tier still runs through the real
    /// <see cref="WallService"/> and the real variant/response plumbing.
    /// </summary>
    private async Task<Func<HttpContext, Task>> RouteAsync(IWallPanelService panelService)
    {
        var storage = Path.Combine(Path.GetTempPath(), "bwk-panel-fallback", Guid.NewGuid().ToString("N"));
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storage;

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(panelService);
        builder.Services.AddSingleton(harness.WallService);
        builder.Services.AddSingleton(harness.CurrentUser);
        builder.Services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(harness.DbContextFactory);
        builder.Services.AddSingleton(Substitute.For<IKioskContext>());
        builder.Services.AddSingleton<IImageVariantCache>(
            new FileSystemImageVariantCache(settings, NullLogger<FileSystemImageVariantCache>.Instance));

        var app = builder.Build();
        app.MapWallPanelPhotos();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == PanelPhotoRoute);

        var services = app.Services;
        return http =>
        {
            http.RequestServices = services;
            return endpoint.RequestDelegate!(http);
        };
    }

    private DefaultHttpContext Request(Guid panelId)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.RouteValues["wallId"] = harness.WallId.ToString();
        http.Request.RouteValues["panelId"] = panelId.ToString();
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static byte[] Body(HttpContext http) => ((MemoryStream)http.Response.Body).ToArray();
}
