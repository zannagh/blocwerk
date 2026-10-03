// <copyright file="WallRefreshActors.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The services the refresh worker drives, acting as the admin who started the refresh, so every step passes
/// the same guards (wall admin, own capture draft) as when that admin does it by hand.
/// </summary>
public sealed record WallRefreshActors(
    IWallCaptureService Capture,
    ICapturePanelPhotoService PanelPhotos,
    IWallBigUpdateService BigUpdate,
    IWallUpdateSessionService Sessions,
    IHoldTexturePlacementService? Placement);

/// <summary>A set of <see cref="WallRefreshActors"/> and whatever it needs disposed afterwards.</summary>
public sealed class WallRefreshActorScope(WallRefreshActors actors, IAsyncDisposable? owned = null) : IAsyncDisposable
{
    public WallRefreshActors Actors { get; } = actors;

    public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>Builds the services for one step, acting as <c>userId</c>.</summary>
public interface IWallRefreshActorFactory
{
    Task<WallRefreshActorScope> CreateAsync(Guid userId, CancellationToken ct);
}
