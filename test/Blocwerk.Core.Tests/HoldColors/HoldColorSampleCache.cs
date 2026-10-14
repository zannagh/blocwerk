// <copyright file="HoldColorSampleCache.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds.Colors;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// Reads the samples of <see cref="HoldColorWallMeasurement"/> off a database (live holds of a wall whose colour was
/// set, not proposed) and keeps them in a local file, so a change to the estimator is measured again in seconds.
/// </summary>
internal static class HoldColorSampleCache
{
    public static async Task<List<HoldColorSample>> LoadAsync(string connectionString, string cacheFile, string wallName)
    {
        if (File.Exists(cacheFile))
        {
            return Read(cacheFile);
        }

        var samples = await FromDatabaseAsync(connectionString, wallName);
        Write(cacheFile, samples);
        return samples;
    }

    private static async Task<List<HoldColorSample>> FromDatabaseAsync(string connectionString, string wallName)
    {
        await using var db = TestDb.Create(connectionString);
        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Name == wallName);
        var holds = await db.Holds.AsNoTracking()
            .Where(h => h.WallId == wall.Id && h.Generation == wall.CurrentGeneration && !h.IsVirtual)
            .Where(h => h.Color != null && h.ProposedColor == null)
            .ToListAsync();
        var samples = new List<HoldColorSample>();
        foreach (var onPhoto in holds.GroupBy(h => h.WallPanelId))
        {
            var photo = onPhoto.Key is { } panelId
                ? await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync()
                : wall.Photo;
            using var pixels = HoldPhotoPixels.TryOpen(photo);
            if (pixels is null)
            {
                continue;
            }

            samples.AddRange(onPhoto.Select(h => Sample(h, pixels)));
        }

        return samples;
    }

    private static HoldColorSample Sample(Hold hold, HoldPhotoPixels pixels)
    {
        var (own, surroundings, _) = HoldColorSampler.Sample(hold, pixels.Width, pixels.Height, pixels.At);
        return new HoldColorSample(hold.Id, hold.Color!, own, surroundings);
    }

    private static void Write(string file, List<HoldColorSample> samples)
    {
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write(samples.Count);
        foreach (var s in samples)
        {
            writer.Write(s.HoldId.ToString());
            writer.Write(s.Stored);
            WritePixels(writer, s.Own);
            WritePixels(writer, s.Surroundings);
        }
    }

    private static List<HoldColorSample> Read(string file)
    {
        using var reader = new BinaryReader(File.OpenRead(file));
        var count = reader.ReadInt32();
        var samples = new List<HoldColorSample>(count);
        for (var i = 0; i < count; i++)
        {
            samples.Add(new HoldColorSample(Guid.Parse(reader.ReadString()), reader.ReadString(), ReadPixels(reader), ReadPixels(reader)));
        }

        return samples;
    }

    private static void WritePixels(BinaryWriter writer, List<OklabColor> pixels)
    {
        writer.Write(pixels.Count);
        foreach (var p in pixels)
        {
            writer.Write(p.L);
            writer.Write(p.A);
            writer.Write(p.B);
        }
    }

    private static List<OklabColor> ReadPixels(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var pixels = new List<OklabColor>(count);
        for (var i = 0; i < count; i++)
        {
            pixels.Add(new OklabColor(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()));
        }

        return pixels;
    }
}
