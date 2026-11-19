// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Setting a boulder's style tags on their own.</summary>
public partial interface IBoulderService
{
    /// <summary>
    /// Switches ONE style tag of a boulder on or off without touching anything else, so existing boulders can be tagged one
    /// tap at a time. The change is applied to the value stored now, not to what the caller last saw, so two people tagging
    /// the same boulder do not overwrite each other. Allowed for the same people as editing it: its creator, one of its setters, or an admin or owner of its
    /// wall; anybody else is refused with an <see cref="InvalidOperationException"/>, as is anything but one known style.
    /// </summary>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="style">The one style to switch.</param>
    /// <param name="on">True to add it, false to remove it.</param>
    /// <returns>The boulder, with the tags as stored afterwards.</returns>
    Task<Boulder> SetBoulderStyleAsync(Guid boulderId, BoulderStyle style, bool on);
}

/// <summary>Implements <see cref="IBoulderService.SetBoulderStyleAsync"/>.</summary>
public partial class BoulderService
{
    /// <summary>The refusal for somebody who may not edit the boulder; the Server pages also show it.</summary>
    public const string StylesNotAllowedMessage = "Only the creator, a setter, or a wall admin can set a boulder's styles";

    /// <inheritdoc />
    public async Task<Boulder> SetBoulderStyleAsync(Guid boulderId, BoulderStyle style, bool on)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.SetStyles");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders
                              .Include(b => b.Setters)
                              .FirstOrDefaultAsync(b => b.Id == boulderId)
                          ?? throw new InvalidOperationException("Boulder not found");

            if (!await CanManageBoulderAsync(db, boulder, user.Id))
            {
                _logger.LogWarning("Set styles denied: user {UserId} may not edit boulder {BoulderId}", user.Id, boulderId);
                throw new InvalidOperationException(StylesNotAllowedMessage);
            }

            if (!BoulderStyles.All.Contains(style))
            {
                throw new InvalidOperationException("Unknown style");
            }

            var normalized = BoulderStyles.Normalize(on ? boulder.Styles | style : boulder.Styles & ~style);
            if (boulder.Styles != normalized)
            {
                boulder.Styles = normalized;
                await db.SaveChangesAsync();
                _logger.LogInformation("Boulder {BoulderId} styles set to {Styles} by {UserId}", boulderId, normalized, user.Id);
            }

            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }
}
