// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Setting a boulder's hold types on their own.</summary>
public partial interface IBoulderService
{
    /// <summary>
    /// Sets the hold types a boulder is mainly climbed on (any combination of grips and hooks), or clears them
    /// (<see cref="BoulderHoldType.None"/>) so its holds decide again (<see cref="BoulderHoldTypes.Effective"/>). Allowed
    /// for the same people as editing it: its creator, one of its setters, or an admin or owner of its wall; anybody else
    /// is refused with an <see cref="InvalidOperationException"/>, as is a value outside the known types.
    /// </summary>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="holdTypes">The types, or none to clear them.</param>
    /// <returns>The boulder, with the types as stored afterwards.</returns>
    Task<Boulder> SetBoulderHoldTypesAsync(Guid boulderId, BoulderHoldType holdTypes);
}

/// <summary>Implements <see cref="IBoulderService.SetBoulderHoldTypesAsync"/>.</summary>
public partial class BoulderService
{
    /// <summary>The refusal for somebody who may not edit the boulder; the Server pages also show it.</summary>
    public const string HoldTypeNotAllowedMessage = "Only the creator, a setter, or a wall admin can set a boulder's hold types";

    /// <inheritdoc />
    public async Task<Boulder> SetBoulderHoldTypesAsync(Guid boulderId, BoulderHoldType holdTypes)
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
                _logger.LogWarning("Set hold types denied: user {UserId} may not edit boulder {BoulderId}", user.Id, boulderId);
                throw new InvalidOperationException(HoldTypeNotAllowedMessage);
            }

            EnsureKnownHoldTypes(holdTypes);

            if (boulder.HoldTypes != holdTypes)
            {
                boulder.HoldTypes = holdTypes;
                await db.SaveChangesAsync();
                _logger.LogInformation("Boulder {BoulderId} hold types set to {HoldTypes} by {UserId}", boulderId, holdTypes, user.Id);
            }

            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    // Refuses any bit outside the known types (none, clearing, is fine).
    private static void EnsureKnownHoldTypes(BoulderHoldType holdTypes)
    {
        if (BoulderHoldTypes.Normalize(holdTypes) != holdTypes)
        {
            throw new InvalidOperationException("Unknown hold type");
        }
    }
}
