// <copyright file="DelegateRefreshActors.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Refresh;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>Builds the worker's services for a user through a test delegate.</summary>
internal sealed class DelegateRefreshActors(Func<Guid, Task<WallRefreshActors>> build) : IWallRefreshActorFactory
{
    public async Task<WallRefreshActorScope> CreateAsync(Guid userId, CancellationToken ct) => new(await build(userId));
}
