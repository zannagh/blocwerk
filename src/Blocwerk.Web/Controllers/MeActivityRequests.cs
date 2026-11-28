// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>PATCH /api/v1/me/progression</c>: the fields sent change, the others stay.</summary>
public sealed class ProgressionPreferenceRequest
{
    /// <summary>The progression window in days, 7 to 365.</summary>
    public int? WindowDays { get; set; }

    /// <summary>Day, Week or Month.</summary>
    public string? GroupBy { get; set; }
}

/// <summary>Body of <c>PATCH /api/v1/me/activities/{id}</c>.</summary>
public sealed class ActivityDurationRequest
{
    /// <summary>The activity's length in minutes, 0 to 1440.</summary>
    public int DurationMinutes { get; set; }
}
