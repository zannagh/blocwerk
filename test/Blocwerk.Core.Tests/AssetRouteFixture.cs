// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Resolution;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall holding one hashed asset of every kind the <c>/a/</c> routes serve, plus a second wall owned by somebody
/// else, and the real registered endpoints to call. <see cref="Caller"/> and <see cref="Kiosk"/> set who is asking.
/// </summary>
public sealed class AssetRouteFixture : IDisposable
{
    public const string ShareToken = "share-token-a";

    private readonly string storage = Path.Combine(Path.GetTempPath(), "bwk-asset-routes", Guid.NewGuid().ToString("N"));
    private WebApplication? app;

    public AssetRouteFixture(PostgresTestDatabase? postgres = null)
    {
        Harness = new WallTestHarness(postgres);
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storage;
        ImageStorage = new FileSystemWallImageStorage(settings);
        CaptureFiles = new FileSystemCaptureFileStore(settings);
        Variants = new FileSystemImageVariantCache(settings, NullLogger<FileSystemImageVariantCache>.Instance);
        Kiosk = Substitute.For<IKioskContext>();
        CurrentUser = Substitute.For<ICurrentUserService>();
        CurrentUser.GetCurrentUserAsync().Returns(_ => Caller is { } user
            ? Task.FromResult(user)
            : Task.FromException<User>(new UnauthorizedAccessException()));
        Caller = Harness.Owner;
    }

    public WallTestHarness Harness { get; }

    public IWallImageStorage ImageStorage { get; }

    public ICaptureFileStore CaptureFiles { get; }

    public IImageVariantCache Variants { get; }

    public IKioskContext Kiosk { get; }

    public ICurrentUserService CurrentUser { get; }

    /// <summary>Who is signed in; null for an anonymous caller. Defaults to the wall's owner.</summary>
    public User? Caller { get; set; }

    /// <summary>The panel photo: a real JPEG wide enough for every rendition on the ladder.</summary>
    public byte[] PanelBytes { get; } = TestImages.Noise(2600, 1300, SKEncodedImageFormat.Jpeg, 85);

    public SeededAssets Seeded { get; private set; } = null!;

    public AssetResolver Resolver() =>
        new(Harness.DbContextFactory, CurrentUser, ImageStorage, CaptureFiles, new FileHashMemo(), NullLogger<AssetResolver>.Instance, Kiosk);

    /// <summary>Makes this a kiosk tablet bound to <paramref name="wallId"/>.</summary>
    public void BindKiosk(Guid wallId)
    {
        Kiosk.IsKiosk.Returns(true);
        Kiosk.KioskWallId.Returns(wallId);
    }

    public async Task<SeededAssets> SeedAsync()
    {
        await Harness.SeedWallAsync(holdCount: 0);
        var wallId = Harness.WallId;
        await using var db = Harness.CreateContext();
        var wall = await db.Walls.FindAsync(wallId);
        wall!.ShareToken = ShareToken;

        var panel = new WallPanel { WallId = wallId, Photo = PanelBytes, PhotoContentType = "image/jpeg" };
        db.WallPanels.Add(panel);
        db.WallResets.Add(new WallReset { WallId = wallId, ResetByUserId = Harness.Owner.Id, PreviousPhoto = [4, 4, 4] });
        db.WallImages.Add(await GalleryImageAsync(wallId, [5, 5, 5, 5]));
        var (texture, mask, splat, lod) = await GeometryAsync(db, wallId);
        var video = await VideoAsync(db, wallId, isDraft: false, [7, 7, 7]);
        var draftVideo = await VideoAsync(db, wallId, isDraft: true, [8, 8, 8]);
        var (otherWallId, otherPanel) = AddOtherWall(db);
        await db.SaveChangesAsync();

        Seeded = new SeededAssets(
            Hex(PanelBytes), panel.Id, Hex([1, 2, 3]), Hex([4, 4, 4]), Hex([5, 5, 5, 5]), texture, mask, splat, lod,
            video, draftVideo, otherWallId, Hex(otherPanel));
        return Seeded;
    }

    /// <summary>Calls the real <c>/a/</c> endpoint in-process.</summary>
    public async Task<DefaultHttpContext> GetAsync(
        Guid wallId, string sha, int? width = null, string? token = null, string method = "GET", Action<HttpContext>? configure = null)
    {
        app ??= BuildApp();
        var route = width is null ? AssetEndpoints.Route : AssetEndpoints.VariantRoute;
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == route);

        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Request.Method = method;
        http.Request.RouteValues["wallId"] = wallId.ToString();
        http.Request.RouteValues["sha"] = sha;
        if (width is { } w)
        {
            http.Request.RouteValues["width"] = w.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    public static byte[] Body(HttpContext http) => ((MemoryStream)http.Response.Body).ToArray();

    public static string Hex(byte[] bytes) => ContentHash.ToHex(ContentHash.Of(bytes))!;

    public void Dispose()
    {
        (app as IDisposable)?.Dispose();
        Harness.Dispose();
        if (Directory.Exists(storage))
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAssetResolver>(Resolver());
        builder.Services.AddSingleton(Variants);
        var built = builder.Build();
        built.MapAssets();
        return built;
    }

    private async Task<WallImage> GalleryImageAsync(Guid wallId, byte[] bytes)
    {
        var stored = $"{Guid.NewGuid():N}.jpg";
        await File.WriteAllBytesAsync(ImageStorage.ResolvePhysicalPath(stored)!, bytes);
        return new WallImage
        {
            WallId = wallId, StoragePath = stored, ContentType = "image/jpeg", SizeBytes = bytes.Length, Sha256 = ContentHash.Of(bytes),
        };
    }

    private async Task<(string Texture, string Mask, string Splat, string Lod)> GeometryAsync(
        Blocwerk.Core.Data.BlocwerkDbContext db, Guid wallId)
    {
        byte[] texture = [10, 10], mask = [11, 11], splat = [12, 12, 12, 12], lod = [13, 13];
        var model = new WallGeometryModel { WallId = wallId, Json = "{}", Source = "test" };
        db.WallGeometryModels.Add(model);
        db.WallGeometryTextures.Add(new WallGeometryTexture
        {
            GeometryModelId = model.Id, FacetId = "F1", StoredPath = await CaptureFiles.SaveAsync(texture, ".jpg", default),
            SizeBytes = texture.Length, Sha256 = ContentHash.Of(texture),
            MaskStoredPath = await CaptureFiles.SaveAsync(mask, ".png", default), MaskSizeBytes = mask.Length, MaskSha256 = ContentHash.Of(mask),
        });
        var lodStored = await CaptureFiles.SaveAsync(lod, ".spz", default);
        db.WallGeometrySplats.Add(new WallGeometrySplat
        {
            GeometryModelId = model.Id, StoredPath = await CaptureFiles.SaveAsync(splat, ".spz", default), SizeBytes = splat.Length,
            Sha256 = ContentHash.Of(splat), FrameJson = "{}",
            LodLevelsJson = SplatLodLadder.Serialize([new SplatLodLevel(40_000, lodStored, lod.Length, Hex(lod))]),
        });
        return (Hex(texture), Hex(mask), Hex(splat), Hex(lod));
    }

    private Task<string> VideoAsync(Blocwerk.Core.Data.BlocwerkDbContext db, Guid wallId, bool isDraft, byte[] thumbnail)
    {
        var boulder = new Boulder { WallId = wallId, Name = $"B{thumbnail[0]}", CreatedByUserId = Harness.Owner.Id, IsDraft = isDraft };
        db.Boulders.Add(boulder);
        db.BetaVideos.Add(new BetaVideo
        {
            BoulderId = boulder.Id, UploadedByUserId = Harness.Owner.Id, ContentType = "video/mp4", Thumbnail = thumbnail,
        });
        return Task.FromResult(Hex(thumbnail));
    }

    private (Guid WallId, byte[] PanelBytes) AddOtherWall(Blocwerk.Core.Data.BlocwerkDbContext db)
    {
        var stranger = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
        var wall = new Wall { Name = "Other", OwnerId = stranger.Id, Photo = [20, 20] };
        byte[] panel = [21, 21, 21];
        db.Users.Add(stranger);
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = stranger.Id, Role = WallRole.Admin });
        db.WallPanels.Add(new WallPanel { WallId = wall.Id, Photo = panel, PhotoContentType = "image/jpeg" });
        return (wall.Id, panel);
    }
}
