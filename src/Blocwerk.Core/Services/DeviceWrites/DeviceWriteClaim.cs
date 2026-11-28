// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services.DeviceWrites;

/// <summary>The outcome of <see cref="DeviceWriteReceiptService.ClaimAsync"/>.</summary>
/// <param name="State">What the key's receipt says.</param>
/// <param name="SubjectId">The row the earlier write made, for <see cref="DeviceWriteClaimState.Replay"/>.</param>
public sealed record DeviceWriteClaim(DeviceWriteClaimState State, Guid? SubjectId = null)
{
    /// <summary>A write without a replay key, or with a new one.</summary>
    public static DeviceWriteClaim Fresh { get; } = new(DeviceWriteClaimState.Fresh);
}
