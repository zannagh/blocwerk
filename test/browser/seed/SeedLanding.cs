// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// The Landing wall (Lena's), which only the browse-landing spec lists or counts, with a share link of its own
/// (<see cref="ShareToken"/>). Five boulders, newest last: <b>Fav one</b> and <b>Fav two</b>, which Lena marked as
/// favourites, then <b>Plain one</b>, <b>Plain two</b> and <b>Plain three</b>, which are her projects. So the landing
/// reads All 5, My projects 3, Favorites 2.
/// </summary>
internal static class SeedLanding
{
    /// <summary>The wall's share link, for viewing it signed out.</summary>
    public const string ShareToken = "browser-landing-share-token";

    public static void Add(BlocwerkDbContext db, Wall wall, Guid lenaId)
    {
        wall.ShareToken = ShareToken;
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        string[] names = ["Fav one", "Fav two", "Plain one", "Plain two", "Plain three"];
        for (var i = 0; i < names.Length; i++)
        {
            var boulder = new Boulder { WallId = wall.Id, Name = names[i], CreatedByUserId = lenaId, CreatedAt = start.AddMinutes(i) };
            db.Boulders.Add(boulder);
            if (i < 2)
            {
                db.BoulderFavorites.Add(new BoulderFavorite { BoulderId = boulder.Id, UserId = lenaId });
            }
            else
            {
                db.BoulderProjects.Add(new BoulderProject { BoulderId = boulder.Id, UserId = lenaId });
            }
        }
    }
}
