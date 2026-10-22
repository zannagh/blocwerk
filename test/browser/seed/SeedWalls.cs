// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// The harness's fixed world:
/// <list type="bullet">
/// <item><b>alpha</b>: Alice's wall with two panels, an active 3D model (texture, mask, splat), a boulder with a beta
/// video thumbnail, a kiosk key, and Carol as a member who consented to the kiosk (no PIN).</item>
/// <item>alpha also has a share link (<see cref="SeededWorld.ShareToken"/>).</item>
/// <item><b>beta</b>: Alice's second wall, one panel, and a boulder list long enough to scroll.</item>
/// <item><b>bobs</b>: Bob's wall, one panel. Bob is a member of nothing of Alice's.</item>
/// <item><b>social</b>: Erin's wall of comments and activity, with a kiosk key of its own and users of its own (Erin, Cora,
/// Dave), so no other test's walls or pages change with it (<see cref="SeedSocial"/>).</item>
/// </list>
/// </summary>
internal sealed class SeedWalls
{
    private readonly BlocwerkDbContext db;
    private readonly FileSystemCaptureFileStore captures;

    public SeedWalls(BlocwerkDbContext db, BlocwerkSettings settings)
    {
        this.db = db;
        captures = new FileSystemCaptureFileStore(settings);
    }

    public async Task<SeededWorld> SeedAsync()
    {
        var alice = AddUser("alice@browser.test", "Alice");
        var bob = AddUser("bob@browser.test", "Bob");
        var carol = AddUser("carol@browser.test", "Carol");
        var erin = AddUser("erin@browser.test", "Erin");
        var cora = AddUser("cora@browser.test", "Cora");
        var dave = AddUser("dave@browser.test", "Dave");

        var alpha = AddWall("Alpha", alice, panels: 2, seed: 1);
        db.WallMembers.Add(new WallMember { WallId = alpha.Wall.Id, UserId = carol.Id, Role = WallRole.Member, KioskConsentedAt = DateTimeOffset.UtcNow });
        var geometry = await AddGeometryAsync(alpha.Wall.Id);
        var thumbnail = AddVideoThumbnail(alpha.Wall.Id, alice.Id);
        var kioskToken = AddKioskKey(alpha.Wall.Id, alice.Id);
        alpha.Wall.ShareToken = "browser-harness-share-token";

        var beta = AddWall("Beta", alice, panels: 1, seed: 10);
        AddListBoulders(beta.Wall.Id, alice.Id, 40);
        var bobs = AddWall("Bobs", bob, panels: 1, seed: 20);
        var socialWall = AddWall("Social", erin, panels: 1, seed: 30);
        var social = SeedSocial.Add(db, socialWall.Wall.Id, AddKioskKey(socialWall.Wall.Id, erin.Id), erin.Id, cora.Id, dave.Id);
        await db.SaveChangesAsync();

        return new SeededWorld(
            new Dictionary<string, SeededUser>
            {
                ["alice"] = new(alice.Id, alice.DisplayName),
                ["bob"] = new(bob.Id, bob.DisplayName),
                ["carol"] = new(carol.Id, carol.DisplayName),
                ["erin"] = new(erin.Id, erin.DisplayName),
                ["cora"] = new(cora.Id, cora.DisplayName),
                ["dave"] = new(dave.Id, dave.DisplayName),
            },
            new Dictionary<string, SeededWall>
            {
                ["alpha"] = new(alpha.Wall.Id, alpha.Shas, geometry.Texture, geometry.Mask, geometry.Splat, thumbnail),
                ["beta"] = new(beta.Wall.Id, beta.Shas, null, null, null, null),
                ["bobs"] = new(bobs.Wall.Id, bobs.Shas, null, null, null, null),
                ["social"] = new(socialWall.Wall.Id, socialWall.Shas, null, null, null, null),
            },
            kioskToken,
            alpha.Wall.ShareToken,
            social);
    }

    private static string Hex(byte[] bytes) => ContentHash.ToHex(ContentHash.Of(bytes))!;

    private User AddUser(string email, string name)
    {
        var user = new User { Identifier = $"{name}__{email}", DisplayName = name, Email = email, EmailVerified = true };
        db.Users.Add(user);
        return user;
    }

    private (Wall Wall, List<string> Shas) AddWall(string name, User owner, int panels, int seed)
    {
        var wall = new Wall { Name = name, OwnerId = owner.Id };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = owner.Id, Role = WallRole.Admin });

        var shas = new List<string>();
        for (var col = 0; col < panels; col++)
        {
            // Wider than the fit cap (1920) so every rendition the fill asks for is a real downscale.
            var photo = SeedImages.Jpeg(2048, 1024, seed + col);
            db.WallPanels.Add(new WallPanel { WallId = wall.Id, Col = col, Row = 0, Photo = photo, PhotoContentType = "image/jpeg" });
            shas.Add(Hex(photo));
        }

        return (wall, shas);
    }

    private async Task<(string Texture, string Mask, string Splat)> AddGeometryAsync(Guid wallId)
    {
        var texture = SeedImages.Jpeg(512, 512, 99);
        var mask = SeedImages.Png(256, 256);
        var splat = SeedImages.Bytes(4096, 7);
        var model = new WallGeometryModel { WallId = wallId, Json = "{}", Source = "browser-seed", IsActive = true };
        db.WallGeometryModels.Add(model);
        db.WallGeometryTextures.Add(new WallGeometryTexture
        {
            GeometryModelId = model.Id, FacetId = "F1",
            StoredPath = await captures.SaveAsync(texture, ".jpg", default), SizeBytes = texture.Length, Sha256 = ContentHash.Of(texture),
            MaskStoredPath = await captures.SaveAsync(mask, ".png", default), MaskSizeBytes = mask.Length, MaskSha256 = ContentHash.Of(mask),
        });
        db.WallGeometrySplats.Add(new WallGeometrySplat
        {
            GeometryModelId = model.Id, StoredPath = await captures.SaveAsync(splat, ".spz", default),
            SizeBytes = splat.Length, Sha256 = ContentHash.Of(splat), SplatCount = 1000, FrameJson = "{}",
        });
        return (Hex(texture), Hex(mask), Hex(splat));
    }

    // Plain boulders (no photo, no holds) one minute apart, so the newest-first list has a fixed order.
    private void AddListBoulders(Guid wallId, Guid setterId, int count)
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 1; i <= count; i++)
        {
            db.Boulders.Add(new Boulder { WallId = wallId, Name = $"List boulder {i:00}", CreatedByUserId = setterId, CreatedAt = start.AddMinutes(i) });
        }
    }

    private string AddVideoThumbnail(Guid wallId, Guid uploaderId)
    {
        var thumbnail = SeedImages.Jpeg(320, 180, 42);
        var boulder = new Boulder { WallId = wallId, Name = "Seeded boulder", CreatedByUserId = uploaderId };
        db.Boulders.Add(boulder);
        db.BetaVideos.Add(new BetaVideo { BoulderId = boulder.Id, UploadedByUserId = uploaderId, ContentType = "video/mp4", Thumbnail = thumbnail });
        return Hex(thumbnail);
    }

    private string AddKioskKey(Guid wallId, Guid ownerId)
    {
        var (token, prefix) = ApiKeyTokens.Create();
        db.ApiKeys.Add(new ApiKey
        {
            Name = "Browser harness tablet", Scope = ApiKeyScope.Kiosk, UserId = ownerId, WallId = wallId,
            KeyHash = ApiKeyTokens.Hash(token), Prefix = prefix,
        });
        return token;
    }
}

internal sealed record SeededWorld(
    Dictionary<string, SeededUser> Users, Dictionary<string, SeededWall> Walls, string KioskToken, string ShareToken, SeededSocial Social);

internal sealed record SeededUser(Guid Id, string Name);

internal sealed record SeededWall(Guid Id, List<string> Panels, string? Texture, string? Mask, string? Splat, string? Thumbnail);
