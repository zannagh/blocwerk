// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.ThreeD;

/// <summary>
/// The photo / 3D switch of the wall view and the boulder detail, shared by the two slots it spans: the switch in the
/// header's actions (<see cref="View3DSwitch"/>) and the stage that shows the 3D view in the photos' place
/// (<see cref="Stage3D"/>). One page shows at a time, so one key is enough; another wall or boulder starts on the photos,
/// as a Server page does.
/// </summary>
public sealed class View3DToggle
{
    private (Guid Wall, Guid? Boulder)? shown;

    /// <summary>Raised when the switch flips.</summary>
    public event Action? Changed;

    /// <summary>Whether the page of <paramref name="wallId"/> (and <paramref name="boulderId"/>) shows its 3D view.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="boulderId">The boulder of a boulder page, else null.</param>
    /// <returns>True on the 3D view.</returns>
    public bool IsShown(Guid wallId, Guid? boulderId) => shown == (wallId, boulderId);

    /// <summary>Shows the page's 3D view, or its photos again.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="boulderId">The boulder of a boulder page, else null.</param>
    /// <param name="on">True for the 3D view.</param>
    public void Set(Guid wallId, Guid? boulderId, bool on)
    {
        if (IsShown(wallId, boulderId) == on)
        {
            return;
        }

        shown = on ? (wallId, boulderId) : null;
        Changed?.Invoke();
    }
}
