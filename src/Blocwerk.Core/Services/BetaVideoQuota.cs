// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The per-user disk quota on beta clips: at most <see cref="BetaVideoSettings.QuotaBytes"/> of uploads (the stored
/// originals' sizes) within the last <see cref="BetaVideoSettings.QuotaWindowDays"/> days, whatever wall they are on.
/// Checked before an upload is read (with its declared length) and again once its real size is known.
/// </summary>
public sealed class BetaVideoQuota(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUser,
    BlocwerkSettings settings,
    TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>
    /// The message to refuse an upload of <paramref name="incomingBytes"/> with, or null when it fits (or the quota is off).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Nobody is signed in.</exception>
    public async Task<string?> RefusalAsync(long incomingBytes, CancellationToken ct)
    {
        var options = settings.BetaVideo;
        if (options.QuotaBytes <= 0)
        {
            return null;
        }

        var user = await currentUser.GetCurrentUserAsync();
        var since = clock.GetUtcNow().AddDays(-options.QuotaWindowDays);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var sizes = await db.BetaVideos.AsNoTracking()
            .Where(v => v.UploadedByUserId == user.Id && v.CreatedAt >= since)
            .Select(v => v.SizeBytes)
            .ToListAsync(ct);
        var used = sizes.Sum();
        if (used + Math.Max(0, incomingBytes) <= options.QuotaBytes)
        {
            return null;
        }

        return $"You have uploaded {Gb(used)} of beta videos in the last {options.QuotaWindowDays} days; the limit is "
            + $"{Gb(options.QuotaBytes)}. Delete an older clip or try again later.";
    }

    private static string Gb(long bytes) => $"{bytes / (1024d * 1024 * 1024):0.#} GB";
}
