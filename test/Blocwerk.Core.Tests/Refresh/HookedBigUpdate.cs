// <copyright file="HookedBigUpdate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>The real big update, with a hook that runs before each resume (to fail it, or to act as the user meanwhile).</summary>
internal sealed class HookedBigUpdate(IWallBigUpdateService inner, Func<Task> beforeResume) : IWallBigUpdateService
{
    public Task<BigUpdateSession> StageAsync(Guid wallId, IReadOnlyList<BigUpdatePhoto> photos, bool takeOverExisting = false) =>
        inner.StageAsync(wallId, photos, takeOverExisting);

    public Task<BigUpdateSession> GetStagedAsync(Guid wallId) => inner.GetStagedAsync(wallId);

    public async Task<BigUpdateSession> ResumeAsync(Guid wallId, bool use3DEvidence = false)
    {
        await beforeResume();
        return await inner.ResumeAsync(wallId, use3DEvidence);
    }

    public Task PromoteAsync(Guid wallId, BigUpdateConfirmation confirmation, Guid? expectedSessionId = null) =>
        inner.PromoteAsync(wallId, confirmation, expectedSessionId);

    public Task DiscardAsync(Guid wallId, Guid? expectedSessionId = null) => inner.DiscardAsync(wallId, expectedSessionId);
}
