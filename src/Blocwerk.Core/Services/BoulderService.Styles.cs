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
    /// Replaces the style tags of a boulder without touching anything else, so existing boulders can be tagged one tap
    /// at a time. Allowed for the same people as editing it: its creator, one of its setters, or an admin or owner of its
    /// wall; anybody else is refused with an <see cref="InvalidOperationException"/>. Unknown bits are dropped.
    /// </summary>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="styles">The complete set it should carry afterwards (<see cref="BoulderStyle.None"/> clears it).</param>
    /// <returns>The boulder.</returns>
    Task<Boulder> SetBoulderStylesAsync(Guid boulderId, BoulderStyle styles);
}

/// <summary>Implements <see cref="IBoulderService.SetBoulderStylesAsync"/>.</summary>
public partial class BoulderService
{
    /// <summary>The refusal for somebody who may not edit the boulder; the Server pages also show it.</summary>
    public const string StylesNotAllowedMessage = "Only the creator, a setter, or a wall admin can set a boulder's styles";

    /// <inheritdoc />
    public async Task<Boulder> SetBoulderStylesAsync(Guid boulderId, BoulderStyle styles)
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

            var normalized = BoulderStyles.Normalize(styles);
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
