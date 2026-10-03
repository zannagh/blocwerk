using System.Reflection;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Guards <see cref="WallQueryExtensions.WithoutPhotos"/> against a Wall column being added and forgotten.</summary>
public class WallProjectionTests
{
    private static readonly HashSet<string> Blobs = [nameof(Wall.Photo), nameof(Wall.StagedPhoto)];

    [Fact]
    public async Task WithoutPhotos_CopiesEveryScalarExceptTheBlobs()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        await using var db = h.CreateContext();
        var full = await db.Walls.AsNoTracking().FirstAsync(w => w.Id == h.WallId);

        // Give every scalar a non-default value so a property missing from the projection shows up as a diff.
        full.Description = "d";
        full.StagedPhotoContentType = "image/png";
        full.StagedAt = DateTimeOffset.UtcNow;
        full.StagedByUserId = Guid.NewGuid();
        full.ShareToken = "tok";
        full.Angle = 7;
        full.BorderPoints = [new ShapePoint { Dx = 1, Dy = 2 }];
        full.LastResetAt = DateTimeOffset.UtcNow;
        full.CurrentGeneration = 3;
        full.UsesMultipleImages = true;
        full.UnderMaintenance = true;
        full.MaintenanceByUserId = Guid.NewGuid();
        full.AllowAnonymousKioskSetting = true;
        full.AllowKioskKeyboardShortcuts = true;
        full.LinksFinalizedGeneration = 3;
        full.GlyphsEnabled = true;
        full.MarkerSizeMm = 42.5;
        full.VolumesHaveFlatSides = true;
        full.StagedPhoto = [9];
        db.Walls.Attach(full);
        db.Entry(full).State = EntityState.Modified;
        await db.SaveChangesAsync();

        await using var db2 = h.CreateContext();
        var slim = await db2.Walls.Where(w => w.Id == h.WallId).WithoutPhotos().SingleAsync();
        var reference = await db2.Walls.AsNoTracking().SingleAsync(w => w.Id == h.WallId);

        Assert.Null(slim.Photo);
        Assert.Null(slim.StagedPhoto);
        var scalars = typeof(Wall).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !Blobs.Contains(p.Name) && IsScalar(p.PropertyType));
        foreach (var p in scalars)
        {
            Assert.True(
                Equals(p.GetValue(reference), p.GetValue(slim)) || p.Name == nameof(Wall.BorderPoints),
                $"Wall.{p.Name} is not copied by WithoutPhotos");
        }

        Assert.Single(slim.BorderPoints!);
    }

    private static bool IsScalar(Type t) =>
        !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ICollection<>)) && t != typeof(User);
}
