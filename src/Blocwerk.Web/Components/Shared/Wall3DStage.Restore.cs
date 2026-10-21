// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Web.State.Live;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// A remount of the same wall (and boulder) carries the camera pose and the mode over (<c>options.restore</c> in
/// wall3d.js): a live refresh with new hold outlines or another model must not throw the viewer back to the front view.
/// The viewer has no way to swap its holds in place, so new overlay data still needs a new scene.
/// </summary>
public partial class Wall3DStage
{
    // The view the live viewer was mounted with.
    private Wall3DView? viewerView;

    private async Task<JsonElement?> CaptureRestoreAsync(Wall3DView next)
    {
        if (viewer is not { } current || !Wall3DViewIdentity.SamePlace(viewerView, next))
        {
            return null;
        }

        try
        {
            return await current.InvokeAsync<JsonElement>("state");
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException)
        {
            return null; // the old viewer is gone: the new one starts at its preset
        }
    }
}
