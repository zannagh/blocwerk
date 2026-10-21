// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The words and numbers of Settings → "Offline storage" (<see cref="OfflineStorageCard"/>), kept pure so they are
/// testable without a browser.
/// </summary>
public static class OfflineStorageText
{
    private const double Kilo = 1024d;

    /// <summary>A byte count for people: "0 B", "820 KB" (kilobytes whole), "12.4 MB", "1.2 GB".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < Kilo)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytes)} B");
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / Kilo;
        var unit = 0;
        while (value >= Kilo && unit < units.Length - 1)
        {
            value /= Kilo;
            unit++;
        }

        var format = value >= 100 || unit == 0 ? "0" : "0.0";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>What a wall holds, by part: the photos always, the others when there is any.</summary>
    public static string Breakdown(OfflineWallUsage wall)
    {
        var parts = new List<string> { "Photos " + Bytes(wall.Core) };
        if (wall.Gallery > 0)
        {
            parts.Add("Gallery " + Bytes(wall.Gallery));
        }

        if (wall.View3d > 0)
        {
            parts.Add("3D " + Bytes(wall.View3d));
        }

        if (wall.Other > 0)
        {
            parts.Add("Other " + Bytes(wall.Other));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// How this device treats 3D: on a phone or tablet only when a 3D view is opened (and that can use mobile data),
    /// on a computer in the background on an unmetered connection.
    /// </summary>
    public static string ThreeDNote(OfflineStorageFacts facts)
    {
        if (!facts.Keep3d)
        {
            return "3D data isn't kept on this device. It downloads again each time you open a 3D view.";
        }

        if (facts.Mobile)
        {
            return "On phones and tablets, 3D is saved only when you open a wall's 3D view. That download can be "
                + "large and can use your mobile data.";
        }

        return "On this computer, 3D downloads in the background while you're on Wi-Fi or another unmetered connection.";
    }

    /// <summary>Whether the browser may clear the saved walls on its own.</summary>
    public static string Persistence(OfflineStorageFacts facts)
    {
        if (facts.Persisted)
        {
            return "Storage is persistent: the browser won't clear it on its own.";
        }

        return facts.IosTab
            ? "Storage isn't persistent in a Safari tab."
            : "Storage isn't persistent yet: the browser may clear it when the device runs low on space.";
    }

    /// <summary>A wall's display name: the viewer's own wall by name, anything else (a share link) generically.</summary>
    public static string WallName(OfflineWallUsage wall, IReadOnlyDictionary<Guid, string> names)
    {
        return Guid.TryParse(wall.WallId, out var id) && names.TryGetValue(id, out var name)
            ? name
            : "A shared wall";
    }
}
