// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>wall.manifest</c> from tracked saves: written beside the entity's own row whenever a manifest input changes, once
/// per wall and save, for every viewer of the wall, and never for staged photos.
/// </summary>
public sealed class ManifestFeedMapperTests : IDisposable
{
    private readonly SocialFeedFixture fixture = new();

    [Fact]
    public async Task ACommittedPanelPhoto_WritesOneManifestRow_AStagedOneNone()
    {
        await fixture.SeedAsync();
        var panel = new WallPanel { WallId = fixture.WallId, StagedPhoto = [1, 2] };
        await fixture.AddAsync(panel);
        Assert.DoesNotContain(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest);

        await fixture.ChangeAsync<WallPanel>(p => p.Id == panel.Id, (p, _) => p.Photo = [3, 4]);
        var row = Assert.Single(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest);
        Assert.Equal((fixture.WallId, fixture.WallId, FeedAudience.Wall, (string?)null), (row.WallId, row.EntityId, row.Audience, row.DataJson));

        await fixture.ClearRowsAsync();
        await fixture.ChangeAsync<WallPanel>(p => p.Id == panel.Id, (p, _) => p.StagedPhoto = [5]);
        Assert.Equal([FeedKinds.PanelStaged], (await fixture.RowsAsync()).Select(r => r.Kind)); // for the editors, no manifest
    }

    [Fact]
    public async Task OneSave_TouchingManyInputs_WritesOneRow()
    {
        await fixture.SeedAsync();
        await using (var db = fixture.Context())
        {
            db.WallPanels.Add(new WallPanel { WallId = fixture.WallId, Photo = [1] });
            db.WallPanels.Add(new WallPanel { WallId = fixture.WallId, Col = 1, Photo = [2] });
            db.WallImages.Add(new WallImage { WallId = fixture.WallId, StoragePath = "a.jpg", ContentType = "image/jpeg" });
            await db.SaveChangesAsync();
        }

        Assert.Single(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest);
    }

    [Fact]
    public async Task TheWallsPhoto_AndItsGeneration_AreInputs_ItsSettingsAreNot()
    {
        await fixture.SeedAsync();
        await fixture.ClearRowsAsync();

        await fixture.ChangeAsync<Wall>(w => w.Id == fixture.WallId, (w, _) => w.Name = "Renamed");
        Assert.DoesNotContain(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest);

        await fixture.ChangeAsync<Wall>(w => w.Id == fixture.WallId, (w, _) => w.CurrentGeneration++);
        Assert.Single(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest);

        await fixture.ClearRowsAsync();
        await fixture.ChangeAsync<Wall>(w => w.Id == fixture.WallId, (w, _) => w.Photo = [9, 9, 9]);
        Assert.Single(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest);

        await fixture.ClearRowsAsync();
        await fixture.ChangeAsync<Wall>(w => w.Id == fixture.WallId, (w, _) => w.StagedPhoto = [8]);
        Assert.Empty(await fixture.RowsAsync());
    }

    [Fact]
    public async Task AGalleryUpload_AndItsDelete_AreInputs()
    {
        await fixture.SeedAsync();
        var image = new WallImage { WallId = fixture.WallId, StoragePath = "a.jpg", ContentType = "image/jpeg", Sha256 = new byte[32] };
        await fixture.AddAsync(image);
        Assert.Equal([FeedKinds.GalleryChanged, FeedKinds.WallManifest], (await fixture.RowsAsync()).Select(r => r.Kind).Order());

        await fixture.ClearRowsAsync();
        await fixture.ChangeAsync<WallImage>(i => i.Id == image.Id, (i, db) => db.Remove(i));
        Assert.Equal([FeedKinds.GalleryDeleted, FeedKinds.WallManifest], (await fixture.RowsAsync()).Select(r => r.Kind).Order());
    }

    [Fact]
    public async Task ActivatingAModel_AndATextureOfATrackedModel_AreInputs()
    {
        await fixture.SeedAsync();
        var model = new WallGeometryModel { WallId = fixture.WallId, Json = "{}", Source = "test" };
        await fixture.AddAsync(model);
        Assert.DoesNotContain(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest);

        await fixture.ChangeAsync<WallGeometryModel>(m => m.Id == model.Id, (m, _) => m.IsActive = true);
        Assert.Single(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest);

        await fixture.ClearRowsAsync();
        await fixture.ChangeAsync<WallGeometryModel>(m => m.Id == model.Id, (m, db) => db.Add(new WallGeometryTexture
        {
            GeometryModelId = m.Id, GeometryModel = m, FacetId = "F1", StoredPath = "t.jpg",
        }));
        Assert.Single(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest);
    }

    /// <summary>The capture processor's re-texture and splat swap track the model first, so their rows find the wall.</summary>
    [Fact]
    public async Task ATextureSavedBesideItsTrackedModel_WritesTheManifestRow()
    {
        await fixture.SeedAsync();
        var model = new WallGeometryModel { WallId = fixture.WallId, Json = "{}", Source = "test", IsActive = true };
        await using (var db = fixture.Harness.CreateContext())
        {
            db.WallGeometryModels.Add(model);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.Context())
        {
            await db.WallGeometryModels.FindAsync([model.Id]);
            db.WallGeometryTextures.Add(new WallGeometryTexture { GeometryModelId = model.Id, FacetId = "F1", StoredPath = "t.jpg" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(fixture.WallId, Assert.Single(await fixture.RowsAsync(), r => r.Kind == FeedKinds.WallManifest).WallId);
    }

    /// <summary>A preview install swaps files of an inactive model: not a manifest input.</summary>
    [Fact]
    public async Task ATextureOfATrackedInactiveModel_WritesNothing()
    {
        await fixture.SeedAsync();
        var model = new WallGeometryModel { WallId = fixture.WallId, Json = "{}", Source = "test", IsActive = false };
        await using (var db = fixture.Harness.CreateContext())
        {
            db.WallGeometryModels.Add(model);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.Context())
        {
            await db.WallGeometryModels.FindAsync([model.Id]);
            db.WallGeometryTextures.Add(new WallGeometryTexture { GeometryModelId = model.Id, FacetId = "F1", StoredPath = "t.jpg" });
            db.WallGeometrySplats.Add(new WallGeometrySplat { GeometryModelId = model.Id, StoredPath = "s.spz", FrameJson = "{}" });
            await db.SaveChangesAsync();
        }

        Assert.Empty(await fixture.RowsAsync());
    }

    /// <summary>A texture saved without its model knows no wall (no query in a mapper): it writes nothing, and the cache ages.</summary>
    [Fact]
    public async Task ATextureOfAnUntrackedModel_WritesNothing()
    {
        await fixture.SeedAsync();
        var model = new WallGeometryModel { WallId = fixture.WallId, Json = "{}", Source = "test", IsActive = true };
        await using (var db = fixture.Harness.CreateContext())
        {
            db.WallGeometryModels.Add(model);
            await db.SaveChangesAsync();
        }

        await fixture.AddAsync(new WallGeometryTexture { GeometryModelId = model.Id, FacetId = "F1", StoredPath = "t.jpg" });
        Assert.Empty(await fixture.RowsAsync());
        await using var check = fixture.Harness.CreateContext();
        Assert.Equal(1, await check.WallGeometryTextures.CountAsync());
    }

    public void Dispose() => fixture.Dispose();
}
