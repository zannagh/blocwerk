// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// The wall setting "iPhone scan" (<see cref="Wall.UpdateMode"/>). Read and changed by the wall's owner and admins only,
/// never from a kiosk.
/// </summary>
public interface IWallUpdateModeService
{
    /// <summary>The wall's mode and whether it may be switched back to panels now.</summary>
    Task<WallUpdateModeView> GetAsync(Guid wallId);

    /// <summary>
    /// Sets the mode. Free until a first scan has been applied to the wall (<see cref="Wall.ActiveScanCaptureId"/>); after
    /// that, going back to panels is refused (it happens by starting a panel update).
    /// </summary>
    Task<WallUpdateModeView> SetAsync(Guid wallId, WallUpdateMode mode);
}
