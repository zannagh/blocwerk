// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Documents;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The view3d document (design §6.5): wall-level bytes, the same for every boulder page and viewer, with the boulder and
/// the share token kept out and put back by the overlay.
/// </summary>
public class View3DDocumentTests
{
    [Fact]
    public void ABoulderView_SplitsIntoTheWallsDocument_AndAnOverlayOfItsRoles()
    {
        var (wall, start, _, boulder) = WallWithBoulder();
        var doc = WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson);
        var plain = Wall3DViewBuilder.Build(wall, doc, null);
        var withBoulder = Wall3DViewBuilder.Build(wall, doc, boulder.Id);

        var (document, overlay) = View3DDocument.Split(withBoulder);

        Assert.Null(document.BoulderId);
        Assert.Null(document.BoulderName);
        Assert.All(document.Holds, h => Assert.Null(h.Role));
        Assert.Equal(Sha(View3DDocument.Split(plain).Document), Sha(document));
        Assert.Equal(boulder.Id, overlay.BoulderId);
        Assert.Equal(Wall3DHoldRole.Start, overlay.Roles[start.Id]);
        Assert.Empty(overlay.Holds);
    }

    [Fact]
    public void AHoldDrawnOnlyForTheBoulder_LeavesTheDocument_AndRidesTheOverlay()
    {
        var (wall, _, kept, boulder) = WallWithBoulder();
        kept.KeptUnconfirmedGeneration = 2;
        var doc = WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson);

        var withBoulder = Wall3DViewBuilder.Build(wall, doc, boulder.Id);
        var (document, overlay) = View3DDocument.Split(withBoulder);

        Assert.True(withBoulder.Holds.Single(h => h.Id == kept.Id).BoulderOnly);
        Assert.DoesNotContain(document.Holds, h => h.Id == kept.Id);
        Assert.Equal(kept.Id, Assert.Single(overlay.Holds).Id);
        Assert.DoesNotContain(kept.Id, overlay.Roles.Keys);
        Assert.Equal(Sha(View3DDocument.Split(Wall3DViewBuilder.Build(wall, doc, null)).Document), Sha(document));
        Assert.DoesNotContain("boulderOnly", Json(withBoulder), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheShareToken_IsLeftOutOfTheDocument_AndReportedForThePage()
    {
        var wallId = Guid.NewGuid();
        var sha = new string('a', 64);
        var view = new Wall3DView
        {
            WallId = wallId,
            Textures = [new Wall3DTexture("F1", $"/api/walls/{wallId}/a/{sha}?token=t%2Bk", new PlaneRectMm(0, 1, 0, 1), $"/x/mask?v=3&token=t%2Bk")],
            SplatUrl = $"/api/walls/{wallId}/geometry/m/splat?token=t%2Bk&v=2",
            SplatLevels = [new Wall3DSplatLevel($"/api/walls/{wallId}/a/{sha}?token=t%2Bk", 40_000, 10)],
        };

        var (document, _) = View3DDocument.Split(view);
        var published = new View3DDocumentStore().Publish(view);

        Assert.DoesNotContain("token", Json(document), StringComparison.Ordinal);
        Assert.Equal($"/api/walls/{wallId}/a/{sha}", document.Textures[0].Url);
        Assert.Equal("/x/mask?v=3", document.Textures[0].MaskUrl);
        Assert.Equal($"/api/walls/{wallId}/geometry/m/splat?v=2", document.SplatUrl);
        Assert.Equal("t+k", published.Token);
        Assert.Equal(Sha(document), published.Sha);
    }

    [Fact]
    public void TheDocument_IsSerialisedAsInterop_AndItsShaNamesItsBytes()
    {
        var (wall, _, _, _) = WallWithBoulder();
        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson), null);
        var store = new View3DDocumentStore();

        var published = store.Publish(view);
        var stored = store.Find(view.WallId, published.Sha)!;

        using var parsed = JsonDocument.Parse(stored.Json);
        Assert.Equal(view.WallId, parsed.RootElement.GetProperty("wallId").GetGuid());
        Assert.True(parsed.RootElement.TryGetProperty("holds", out _));
        Assert.Equal(published.Sha, ContentHash.ToHex(ContentHash.Of(stored.Json)));
        Assert.Equal(stored.Json.LongLength, published.Bytes);
        Assert.Null(store.Find(Guid.NewGuid(), published.Sha));
    }

    [Fact]
    public void TheStore_DropsTheLeastRecentlyUsed_AndARebuildBlocksTheNextForAWindow()
    {
        var store = new View3DDocumentStore();
        var firstWall = Guid.NewGuid();
        var first = store.Publish(new Wall3DView { WallId = firstWall, WallName = "first" });
        Assert.NotNull(store.Find(firstWall, first.Sha));
        for (var i = 0; i < View3DDocumentStore.MaxEntries; i++)
        {
            store.Publish(new Wall3DView { WallId = Guid.NewGuid(), WallName = $"w{i}" });
        }

        var wallId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(store.Find(firstWall, first.Sha));
        Assert.True(store.TryBeginRebuild(wallId, null, now));
        Assert.False(store.TryBeginRebuild(wallId, null, now.AddSeconds(30)));
        Assert.True(store.TryBeginRebuild(wallId, Guid.NewGuid(), now));
        Assert.True(store.TryBeginRebuild(wallId, null, now + View3DDocumentStore.RebuildWindow));
    }

    [Fact]
    public void TheDocumentUrl_CarriesTheTokenAndTheBoulderHint()
    {
        var wallId = Guid.NewGuid();
        var boulderId = Guid.NewGuid();
        var sha = new string('b', 64);

        Assert.Equal($"/api/walls/{wallId}/d/view3d/{sha}", AssetUrls.View3DDocument(wallId, sha));
        Assert.Equal($"/api/walls/{wallId}/d/view3d/{sha}?token=a%20b&boulder={boulderId}", AssetUrls.View3DDocument(wallId, sha, "a b", boulderId));
        Assert.Equal($"/api/walls/{wallId}/d/view3d/{sha}?boulder={boulderId}", AssetUrls.View3DDocument(wallId, sha, null, boulderId));
    }

    private static string Json(Wall3DView view) => System.Text.Encoding.UTF8.GetString(View3DDocument.Serialize(view));

    private static string Sha(Wall3DView document) => ContentHash.ToHex(ContentHash.Of(View3DDocument.Serialize(document)))!;

    private static (Wall Wall, Hold Start, Hold Kept, Boulder Boulder) WallWithBoulder()
    {
        var wall = new Wall { Name = "Test", CurrentGeneration = 2 };
        Hold Add(double a, double b)
        {
            var hold = new Hold { WallId = wall.Id, FacetId = "0", PlaneAMm = a, PlaneBMm = b, Generation = 2, Category = HoldCategory.Hand };
            wall.Holds.Add(hold);
            return hold;
        }

        var start = Add(100, 100);
        var kept = Add(1500, 1500);
        Add(900, 900);
        var boulder = new Boulder { Name = "B", WallId = wall.Id };
        boulder.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = start.Id, Type = HoldType.Start, Usage = HoldUsage.HandAndFoot });
        boulder.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = kept.Id, Type = HoldType.Top, Usage = HoldUsage.HandAndFoot });
        wall.Boulders.Add(boulder);
        return (wall, start, kept, boulder);
    }
}
