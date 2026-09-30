// <copyright file="RefreshTimeline.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>The progress timeline of a <see cref="WallRefresh"/> and the JSON columns it is stored in.</summary>
public static class RefreshTimeline
{
    public const string Upload = "upload";
    public const string Sort = "sort";
    public const string Capture = "capture";
    public const string Detect = "detect";
    public const string Match = "match";
    public const string Review = "review";
    public const string Apply = "apply";
    public const string Place = "place";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every step, in order, all pending.</summary>
    public static IReadOnlyList<RefreshStep> Initial() =>
    [
        new(Upload, "Photos and videos uploaded", RefreshStepState.Running),
        new(Sort, "Photos sorted to panels", RefreshStepState.Pending),
        new(Capture, "3D model", RefreshStepState.Pending),
        new(Detect, "Holds detected on the new panel photos", RefreshStepState.Pending),
        new(Match, "Old holds found on the new photos", RefreshStepState.Pending),
        new(Review, "Your check", RefreshStepState.Pending),
        new(Apply, "Panel update applied", RefreshStepState.Pending),
        new(Place, "Holds placed on the 3D model", RefreshStepState.Pending),
    ];

    public static IReadOnlyList<RefreshStep> Steps(WallRefresh refresh) => Read<List<RefreshStep>>(refresh.StepsJson) ?? [.. Initial()];

    /// <summary>Sets one step's state and detail on the row.</summary>
    public static void Set(WallRefresh refresh, string key, RefreshStepState state, string? detail = null)
    {
        var steps = Steps(refresh).Select(s => s.Key == key ? s with { State = state, Detail = detail } : s).ToList();
        refresh.StepsJson = Write(steps);
        refresh.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static IReadOnlyList<PanelPick> Picks(WallRefresh refresh) => Read<List<PanelPick>>(refresh.PanelPicksJson) ?? [];

    public static IReadOnlyList<RefreshVideo> Videos(WallRefresh refresh) => Read<List<RefreshVideo>>(refresh.VideosJson) ?? [];

    public static RefreshSummary? Summary(WallRefresh refresh) => Read<RefreshSummary>(refresh.SummaryJson);

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>
    /// Every stored video name a run may still hand to its capture (the capture sweep keeps them). Once a run is past
    /// its 3D start, failed or discarded, its videos are no longer referenced and the sweep removes them.
    /// </summary>
    public static async Task<IReadOnlyList<string>> StoredVideosAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var json = await db.WallRefreshes
            .Where(r => r.VideosJson != null && (r.Status == WallRefreshStatus.Uploading || r.Status == WallRefreshStatus.Sorting
                        || r.Status == WallRefreshStatus.ReadyToStart || r.Status == WallRefreshStatus.Running))
            .Select(r => r.VideosJson)
            .ToListAsync(ct);
        return json.SelectMany(j => Read<List<RefreshVideo>>(j) ?? []).Select(v => v.StoredName).ToList();
    }

    private static T? Read<T>(string? json)
        where T : class =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Json);
}
