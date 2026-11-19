// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Setting a boulder's dominant hold type on its own.</summary>
public partial interface IBoulderService
{
    /// <summary>
    /// Sets the hold type a boulder is mainly climbed on, or clears it (null) so its holds decide again
    /// (<see cref="BoulderHoldTypes.Effective"/>). Allowed for the same people as editing it: its creator, one of its
    /// setters, or an admin or owner of its wall; anybody else is refused with an <see cref="InvalidOperationException"/>,
    /// as is a value outside the five hold types.
    /// </summary>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="holdType">The type, or null to clear it.</param>
    /// <returns>The boulder, with the type as stored afterwards.</returns>
    Task<Boulder> SetBoulderHoldTypeAsync(Guid boulderId, HoldHandType? holdType);
}

/// <summary>Implements <see cref="IBoulderService.SetBoulderHoldTypeAsync"/>.</summary>
public partial class BoulderService
{
    /// <summary>The refusal for somebody who may not edit the boulder; the Server pages also show it.</summary>
    public const string HoldTypeNotAllowedMessage = "Only the creator, a setter, or a wall admin can set a boulder's hold type";

    /// <inheritdoc />
    public async Task<Boulder> SetBoulderHoldTypeAsync(Guid boulderId, HoldHandType? holdType)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.SetHoldType");
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
                _logger.LogWarning("Set hold type denied: user {UserId} may not edit boulder {BoulderId}", user.Id, boulderId);
                throw new InvalidOperationException(HoldTypeNotAllowedMessage);
            }

            EnsureKnownHoldType(holdType);

            if (boulder.DominantHoldType != holdType)
            {
                boulder.DominantHoldType = holdType;
                await db.SaveChangesAsync();
                _logger.LogInformation("Boulder {BoulderId} hold type set to {HoldType} by {UserId}", boulderId, holdType, user.Id);
            }

            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    // Refuses anything but one of the five hold types (null, clearing, is fine).
    private static void EnsureKnownHoldType(HoldHandType? holdType)
    {
        if (holdType is { } type && BoulderHoldTypes.Definitions.All(d => d.Value != type))
        {
            throw new InvalidOperationException("Unknown hold type");
        }
    }
}
