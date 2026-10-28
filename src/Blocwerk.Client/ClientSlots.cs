// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Gallery;
using Blocwerk.Client.ThreeD;
using Blocwerk.Client.Views;

namespace Blocwerk.Client;

/// <summary>
/// What PR 3.13 mounts into the views' slots (PR 3.11's <see cref="ViewSlotRegistry"/>): the photo / 3D switch in the
/// wall header's and the boulder title's actions, the 3D view in place of the photos on both stages, and the gallery
/// below the wall's boulder list. No view changes for it.
/// </summary>
public static class ClientSlots
{
    /// <summary>Mounts the gallery and 3D components.</summary>
    /// <param name="registry">The registry Program.cs hands the views.</param>
    /// <returns>The registry.</returns>
    public static ViewSlotRegistry Register(ViewSlotRegistry registry) => registry
        .Add<View3DSwitch>(ViewSlotName.WallActions)
        .Add<Stage3D>(ViewSlotName.WallStage)
        .Add<GalleryCard>(ViewSlotName.WallBelow)
        .Add<View3DSwitch>(ViewSlotName.BoulderActions)
        .Add<Stage3D>(ViewSlotName.BoulderStage);
}
