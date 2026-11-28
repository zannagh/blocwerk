// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Pages.Walls;
using Blocwerk.Web.State.Live;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The web wall page's read-only cards for the companion app, from the page's own service calls: the wall and boulder
/// activity (<see cref="IActivityLogService"/>, bylines through <see cref="LeafAuthors"/>), the members' leaderboard
/// (<see cref="IProgressionService.GetWallLeaderboardAsync"/>), the temperature chart (<see cref="IWallTemperatureService"/>
/// over <see cref="WallTemperatureWindow"/>) and the look-over banner (<see cref="IWallLookOverService.GetStateAsync"/>).
/// </summary>
/// <remarks>
/// A linked device key only, members of the wall only (a bare 404 for anyone else, as for an unknown wall), counted in
/// the key's sync bucket, never cached.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeySyncRateLimit]
[Produces("application/json")]
public sealed class DeviceWallReadsController : ControllerBase
{
    private const int MaxPageSize = 50;

    /// <summary>The highest page asked for; far beyond any wall's log, and page * size stays well inside an int.</summary>
    private const int MaxPage = 10_000;

    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly ICurrentUserService currentUser;

    public DeviceWallReadsController(IDbContextFactory<BlocwerkDbContext> contexts, ICurrentUserService currentUser)
    {
        this.contexts = contexts;
        this.currentUser = currentUser;
    }

    /// <summary>The wall's activity, newest first, a page at a time (the web shows 20).</summary>
    [HttpGet("activity")]
    public async Task<IActionResult> GetActivity(
        Guid wallId,
        [FromServices] IActivityLogService activity,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (await CallerAsync(wallId, ct) is not { IsMember: true } caller)
        {
            return NotFound();
        }

        var (size, at) = Paging(page, pageSize);
        return await ReadAsync(async () =>
        {
            var (items, total) = await activity.GetWallActivityAsync(wallId, at, size);
            return Ok(Page(items, total, at, size, caller.UserId));
        });
    }

    /// <summary>One boulder's activity, newest first, a page at a time (the web shows 5).</summary>
    [HttpGet("boulders/{boulderId:guid}/activity")]
    public async Task<IActionResult> GetBoulderActivity(
        Guid wallId,
        Guid boulderId,
        [FromServices] IActivityLogService activity,
        [FromServices] IBoulderService boulders,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 5,
        CancellationToken ct = default)
    {
        if (await CallerAsync(wallId, ct) is not { IsMember: true } caller
            || await boulders.GetBoulderAsync(boulderId) is not { } boulder || boulder.WallId != wallId)
        {
            return NotFound();
        }

        var (size, at) = Paging(page, pageSize);
        return await ReadAsync(async () =>
        {
            var (items, total) = await activity.GetBoulderActivityAsync(boulderId, at, size);
            return Ok(Page(items, total, at, size, caller.UserId));
        });
    }

    /// <summary>The members' leaderboard: hardest send, score and volume on this wall.</summary>
    [HttpGet("leaderboard")]
    public async Task<IActionResult> GetLeaderboard(Guid wallId, [FromServices] IProgressionService progression, CancellationToken ct)
    {
        if (await CallerAsync(wallId, ct) is not { IsMember: true })
        {
            return NotFound();
        }

        var entries = await progression.GetWallLeaderboardAsync(wallId);
        return Ok(entries
            .Select(e => new DeviceLeaderboardEntry(
                e.UserId,
                e.DisplayName,
                e.HasAvatar ? $"/api/users/{e.UserId}/avatar" : null,
                e.HardestSendGrade,
                e.HardestSendScore,
                e.Score,
                e.VolumeMinutes))
            .ToList());
    }

    /// <summary>The latest temperature and one window's bucket averages: range 24h (default), 7d, 30d, 1y or all.</summary>
    [HttpGet("temperature")]
    public async Task<IActionResult> GetTemperature(
        Guid wallId,
        [FromServices] IWallTemperatureService temperatures,
        [FromQuery] string range = "24h",
        CancellationToken ct = default)
    {
        if (!WallTemperatureWindow.TryParse(range, out var span))
        {
            return DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, "range must be 24h, 7d, 30d, 1y or all.");
        }

        if (await CallerAsync(wallId, ct) is not { IsMember: true })
        {
            return NotFound();
        }

        var latest = await temperatures.GetLatestReadingAsync(wallId, ct);
        var to = DateTimeOffset.UtcNow;
        if (await WallTemperatureWindow.ResolveAsync(temperatures, wallId, span, to, ct) is not { } window)
        {
            return Ok(new DeviceTemperatureResponse(latest?.TemperatureCelsius, latest?.RecordedAt, range, null, to, 0, []));
        }

        var buckets = await temperatures.GetBucketedReadingsAsync(wallId, window.From, to, window.Width, ct);
        return Ok(new DeviceTemperatureResponse(
            latest?.TemperatureCelsius, latest?.RecordedAt, range, window.From, to, window.Width.TotalSeconds, buckets));
    }

    /// <summary>The look-over banner: whether one is pending, its findings, and whether the caller may do it.</summary>
    [HttpGet("look-over")]
    public async Task<IActionResult> GetLookOver(Guid wallId, [FromServices] IWallLookOverService lookOver, CancellationToken ct)
    {
        if (await CallerAsync(wallId, ct) is not { IsMember: true })
        {
            return NotFound();
        }

        return await lookOver.GetStateAsync(wallId, ct) is { } state ? Ok(state) : NotFound();
    }

    // A membership revoked between the check and the read: the service refuses, and the answer is the outsider's 404.
    private static async Task<IActionResult> ReadAsync(Func<Task<IActionResult>> read)
    {
        try
        {
            return await read();
        }
        catch (UnauthorizedAccessException)
        {
            return new NotFoundResult();
        }
    }

    private static (int Size, int Page) Paging(int page, int pageSize) =>
        (Math.Clamp(pageSize, 1, MaxPageSize), Math.Clamp(page, 0, MaxPage));

    private static DeviceActivityPage Page(List<ActivityLogEntry> items, int total, int page, int size, Guid viewerId)
    {
        var viewer = FeedViewer.Member(viewerId);
        return new DeviceActivityPage(
            items.Select(item => Item(item, viewer)).ToList(),
            total,
            page,
            size);
    }

    private static DeviceActivityItem Item(ActivityLogEntry item, FeedViewer viewer)
    {
        var author = LeafAuthors.Describe(item.User, viewer);

        // The person a member row is about (joined, role changed, removed, left), named by the same rule as the author.
        var subject = item.SubjectUser is null ? null : LeafAuthors.Describe(item.SubjectUser, viewer);
        return new DeviceActivityItem(
            item.Id,
            item.Type.ToString(),
            author.Id,
            author.Name,
            author is { HasAvatar: true, Id: { } id } ? $"/api/users/{id}/avatar" : null,
            item.BoulderId,
            item.Details,
            item.Timestamp,
            subject?.Id,
            subject?.Name);
    }

    private async Task<WallCaller?> CallerAsync(Guid wallId, CancellationToken ct)
    {
        try
        {
            return await WallCaller.ReadAsync(contexts, currentUser, wallId, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
