// <copyright file="Wall3DViewCacheSettings.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Configuration;

/// <summary>
/// Bounds of the 3D view's in-memory cache (<see cref="Services.Wall3DViewCache"/>). Bound from
/// <c>Blocwerk:Wall3DView:CacheSizeMb</c> / <c>Blocwerk:Wall3DView:CacheSlidingMinutes</c> or the
/// <c>WALL3DVIEW__CACHESIZEMB</c> / <c>WALL3DVIEW__CACHESLIDINGMINUTES</c> environment variables; an unset or
/// out-of-range value keeps the default.
/// </summary>
public sealed class Wall3DViewCacheSettings
{
    /// <summary>Default size limit, MB of estimated entry size.</summary>
    public const int DefaultSizeMb = 64;

    /// <summary>Default sliding expiration, minutes.</summary>
    public const int DefaultSlidingMinutes = 30;

    /// <summary>Gets the size limit in bytes (the estimated in-memory size of the cached, parsed data).</summary>
    public long SizeLimitBytes { get; init; } = DefaultSizeMb * 1024L * 1024;

    /// <summary>Gets how long an entry stays cached without being read.</summary>
    public TimeSpan SlidingExpiration { get; init; } = TimeSpan.FromMinutes(DefaultSlidingMinutes);

    /// <summary>Gets how long an entry stays cached at most, however often it is read.</summary>
    public TimeSpan AbsoluteExpiration { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Binds the settings from <paramref name="configuration"/>; null gives the defaults.</summary>
    /// <param name="configuration">The app configuration (environment variables included), or null.</param>
    /// <returns>The settings.</returns>
    public static Wall3DViewCacheSettings Bind(IConfiguration? configuration)
    {
        string? Read(string key) =>
            configuration?[$"Blocwerk:Wall3DView:{key}"] ?? configuration?[$"Wall3DView:{key}"];

        var sizeMb = ReadInt(Read("CacheSizeMb"), 1, 16 * 1024) ?? DefaultSizeMb;
        var minutes = ReadInt(Read("CacheSlidingMinutes"), 1, 24 * 60) ?? DefaultSlidingMinutes;
        return new Wall3DViewCacheSettings
        {
            SizeLimitBytes = sizeMb * 1024L * 1024,
            SlidingExpiration = TimeSpan.FromMinutes(minutes),
        };
    }

    private static int? ReadInt(string? raw, int min, int max) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : null;
}
