// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Views;

/// <summary>
/// Where a later view (PR 3.13: gallery and 3D) mounts into the wall view and the boulder detail
/// (<see cref="ViewSlot"/>, <see cref="ViewSlotRegistry"/>).
/// </summary>
public enum ViewSlotName
{
    /// <summary>The wall header's actions, beside the wall's name (a gallery or 3D link).</summary>
    WallActions,

    /// <summary>
    /// The wall's photo stage. Its default content is the panel viewer; a registered component gets it as
    /// <see cref="ViewSlot.DefaultContent"/> and shows it or its own view in its place (3D).
    /// </summary>
    WallStage,

    /// <summary>Below the boulder list (the Server's gallery card sits there).</summary>
    WallBelow,

    /// <summary>The boulder title row's actions (the Server's photo / 3D pill).</summary>
    BoulderActions,

    /// <summary>The boulder's photo stage: the panel viewer by default, as <see cref="WallStage"/>.</summary>
    BoulderStage,
}
