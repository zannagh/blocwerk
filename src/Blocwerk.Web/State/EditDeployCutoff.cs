// <copyright file="EditDeployCutoff.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// The moment an editing lease stops holding the deploy gate however present its user is: the first
/// <see cref="EditPresenceOptions.CutoffTimeOfDay"/> in <see cref="EditPresenceOptions.CutoffTimeZone"/> strictly
/// after the lease started.
/// </summary>
/// <remarks>
/// <para><b>Why after the LEASE started and not after the deploy became pending.</b> The server never learns when a
/// deploy became pending: that is the deploy script's knowledge, and a manual deploy reads the same gate. Measured per
/// lease, every lease alive at the cutoff lets go at the cutoff, so the gate opens then for anything pending; only a
/// lease opened after the cutoff can hold it again, and that one is bounded by the next cutoff. In practice the
/// autodeploy poll a minute after midnight finds the gate open.</para>
/// <para><b>Daylight saving.</b> The local time is converted with the zone's rules for that date. A time that does
/// not exist (inside a spring-forward gap) moves forward to the end of the gap; a time that happens twice (fall-back)
/// takes the earlier instant. With the midnight default neither happens in Europe/Berlin, whose switches are at 02:00
/// and 03:00, but the time of day is configurable.</para>
/// </remarks>
public static class EditDeployCutoff
{
    /// <summary>The first cutoff strictly after <paramref name="startedUtc"/>.</summary>
    public static DateTimeOffset NextAfter(DateTimeOffset startedUtc, TimeZoneInfo zone, TimeSpan timeOfDay)
    {
        var startLocal = TimeZoneInfo.ConvertTime(startedUtc, zone);
        var day = startLocal.Date;
        var candidate = ToInstant(day + timeOfDay, zone);
        while (candidate <= startedUtc)
        {
            day = day.AddDays(1);
            candidate = ToInstant(day + timeOfDay, zone);
        }

        return candidate;
    }

    /// <summary>Resolves the configured zone, falling back to UTC for an id this host does not know.</summary>
    public static TimeZoneInfo Zone(EditPresenceOptions options)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(options.CutoffTimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // Inside a spring-forward gap: walk forward a minute at a time to the first time that exists (a gap is at
        // most a couple of hours, so this is bounded).
        while (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(unspecified))
        {
            // Twice in one night: the first occurrence carries the larger (summer) offset.
            var offset = zone.GetAmbiguousTimeOffsets(unspecified).Max();
            return new DateTimeOffset(unspecified, offset);
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
