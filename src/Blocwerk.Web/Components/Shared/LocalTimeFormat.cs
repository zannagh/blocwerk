namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The rendered shape of a <see cref="LocalTime"/> timestamp. The client-side localizer
/// (wwwroot/js/localtime.js) mirrors each value via the lowercase token from
/// <c>LocalTime.FmtKey</c>, so keep the two in lockstep.
/// </summary>
public enum LocalTimeFormat
{
    /// <summary>A calendar date, e.g. "Aug 27, 2026".</summary>
    Date,

    /// <summary>A day-first calendar date, e.g. "27 Aug 2026".</summary>
    DateDmy,

    /// <summary>A short date with 24-hour time, e.g. "Aug 27, 14:05".</summary>
    DateTime,

    /// <summary>A 24-hour time of day, e.g. "14:05".</summary>
    Time,

    /// <summary>A month and year, e.g. "August 2026".</summary>
    MonthYear,

    /// <summary>A weekday with a full date, e.g. "Thursday, 27 Aug 2026".</summary>
    WeekdayDate,

    /// <summary>A weekday, date and 24-hour time, e.g. "Thursday, 27 Aug 2026 14:05".</summary>
    Full,

    /// <summary>A "how long ago" label mirroring <see cref="TimeText.Relative"/>.</summary>
    Relative,
}
