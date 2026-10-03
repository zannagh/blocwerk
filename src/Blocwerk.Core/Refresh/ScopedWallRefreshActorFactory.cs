// <copyright file="ScopedWallRefreshActorFactory.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The app's actor factory: a fresh DI scope per step whose services are built with the refresh's admin as the
/// current user (<see cref="CaptureActingUser"/>), the root database factory and a "not a kiosk" context, the way
/// the capture pipeline acts for the admin who started a capture.
/// </summary>
public sealed class ScopedWallRefreshActorFactory(IServiceScopeFactory scopes, RootDbContextFactory root) : IWallRefreshActorFactory
{
    public async Task<WallRefreshActorScope> CreateAsync(Guid userId, CancellationToken ct)
    {
        await using (var db = root.CreateDbContext())
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                       ?? throw new UserFacingException("The admin who started this update no longer exists.");
            var scope = scopes.CreateAsyncScope();
            try
            {
                return new WallRefreshActorScope(Build(scope.ServiceProvider, new CaptureActingUser(user)), scope);
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }
    }

    private WallRefreshActors Build(IServiceProvider sp, ICurrentUserService acting)
    {
        object[] guarded = [acting, root, new BackgroundKioskContext()];
        object[] plain = [acting, root];
        var capture = ActivatorUtilities.CreateInstance<WallCaptureService>(sp, guarded);
        var panels = ActivatorUtilities.CreateInstance<WallPanelService>(sp, guarded);
        var panelPhotos = ActivatorUtilities.CreateInstance<CapturePanelPhotoService>(sp, capture, panels);
        var bigUpdate = ActivatorUtilities.CreateInstance<WallBigUpdateService>(sp, plain);
        var sessions = ActivatorUtilities.CreateInstance<WallUpdateSessionService>(sp, plain);
        var placement = ActivatorUtilities.CreateInstance<HoldTexturePlacementService>(sp, guarded);
        return new WallRefreshActors(capture, panelPhotos, bigUpdate, sessions, placement);
    }
}
