// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>One row of every entity in <see cref="ContentHashColumn.InDatabase"/>, with every stamped blob filled.</summary>
internal static class ContentHashSeed
{
    /// <summary>Saves the rows (on a seeded wall) and returns each one's type and id.</summary>
    public static async Task<IReadOnlyList<(Type Entity, Guid Id)>> EveryColumnAsync(WallTestHarness h, byte[] bytes)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = "avatar@test", AvatarImage = bytes };
        var wall = new Wall { Name = "Hashed", OwnerId = h.Owner.Id, Photo = bytes, StagedPhoto = bytes };
        var panel = new WallPanel { WallId = wall.Id, Photo = bytes, StagedPhoto = bytes };
        var reset = new WallReset { WallId = wall.Id, PreviousPhoto = bytes, ResetByUserId = h.Owner.Id };
        var boulder = new Boulder { WallId = wall.Id, Name = "Hashed", CreatedByUserId = h.Owner.Id };
        var video = new BetaVideo { BoulderId = boulder.Id, UploadedByUserId = h.Owner.Id, ContentType = "video/mp4", Thumbnail = bytes };
        db.AddRange(user, wall, panel, reset, boulder, video);
        await db.SaveChangesAsync();

        List<(Type, Guid)> rows =
        [
            (typeof(User), user.Id), (typeof(Wall), wall.Id), (typeof(WallPanel), panel.Id),
            (typeof(WallReset), reset.Id), (typeof(BetaVideo), video.Id),
        ];
        var missing = ContentHashColumn.InDatabase.Select(c => c.Entity).Distinct().Except(rows.Select(r => r.Item1)).ToList();
        Assert.True(missing.Count == 0, $"Seed a row of {string.Join(", ", missing.Select(t => t.Name))} here.");
        return rows;
    }

    /// <summary>The stored hash of every stamped column of the seeded rows.</summary>
    public static async Task<IReadOnlyList<(ContentHashColumn Column, byte[]? Hash)>> ReadHashesAsync(
        BlocwerkDbContext db, IReadOnlyList<(Type Entity, Guid Id)> rows)
    {
        var hashes = new List<(ContentHashColumn, byte[]?)>();
        foreach (var (type, id) in rows)
        {
            var entity = await db.FindAsync(type, id);
            Assert.NotNull(entity);
            var entry = db.Entry(entity);
            hashes.AddRange(ContentHashColumn.InDatabase.Where(c => c.Entity == type)
                .Select(c => (c, (byte[]?)entry.Property(c.Hash).CurrentValue)));
        }

        return hashes;
    }
}
