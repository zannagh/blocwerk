// <copyright file="ScopedWallEnhancementActorFactory.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.HoldLinks;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// The app's actor factory: a fresh DI scope per step whose wall services are built with the run's admin as the
/// current user (<see cref="CaptureActingUser"/>), the root context factory and a "not a kiosk" context — the way the
/// "Update panels + 3D" worker acts (<see cref="ScopedWallRefreshActorFactory"/>). The services' own admin checks
/// therefore run in the background exactly as in the UI: an admin who lost the role since cannot enhance.
/// </summary>
public sealed class ScopedWallEnhancementActorFactory(IServiceScopeFactory scopes, RootDbContextFactory root) : IWallEnhancementActorFactory
{
    /// <inheritdoc/>
    public async Task<WallEnhancementActors> CreateAsync(Guid userId, CancellationToken ct)
    {
        await using (var db = root.CreateDbContext())
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                       ?? throw new UserFacingException("The admin who started this enhancement no longer exists.");
            var scope = scopes.CreateAsyncScope();
            try
            {
                return Build(scope.ServiceProvider, new CaptureActingUser(user), scope);
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }
    }

    private WallEnhancementActors Build(IServiceProvider sp, ICurrentUserService acting, AsyncServiceScope scope)
    {
        object[] guarded = [acting, root, new BackgroundKioskContext()];
        return new WallEnhancementActors(
            ActivatorUtilities.CreateInstance<HoldOutlineUpgradeService>(sp, guarded),
            ActivatorUtilities.CreateInstance<HoldStackService>(sp, guarded),
            ActivatorUtilities.CreateInstance<HoldShapeCleanupService>(sp, guarded),
            ActivatorUtilities.CreateInstance<HoldColorProposalService>(sp, guarded),
            ActivatorUtilities.CreateInstance<HoldDuplicateService>(sp, guarded),
            ActivatorUtilities.CreateInstance<HoldLinkSuggestionService>(sp, guarded),
            ActivatorUtilities.CreateInstance<HoldProposalService>(sp, guarded),
            scope);
    }
}
