// <copyright file="WallPhotoEditor.HoldCallbacks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// One click and one pointer-down callback per hold id, made once and handed to every render. A fresh lambda per
/// render made each <see cref="Blocwerk.Shared.Ui.Overlay.HoldShape"/> look changed and re-render with the editor (hundreds of polygons on a
/// big panel, for any state change at all); with stable callbacks a hold that did not change skips its render
/// (<c>HoldShape.ShouldRender</c>). The hold is looked up by id when the event arrives, so a callback made for an
/// earlier copy of the working list (Discard restores fresh objects) still acts on the current one.
/// </summary>
public partial class WallPhotoEditor
{
    private readonly Dictionary<Guid, EventCallback<MouseEventArgs>> holdClickCallbacks = [];
    private readonly Dictionary<Guid, EventCallback<PointerEventArgs>> holdDownCallbacks = [];

    private EventCallback<MouseEventArgs> HoldClickCallback(Guid id)
    {
        if (!holdClickCallbacks.TryGetValue(id, out var callback))
        {
            callback = EventCallback.Factory.Create<MouseEventArgs>(this, () => CurrentHold(id) is { } hold ? OnHoldClick(hold) : Task.CompletedTask);
            holdClickCallbacks[id] = callback;
        }

        return callback;
    }

    private EventCallback<PointerEventArgs> HoldDownCallback(Guid id)
    {
        if (!holdDownCallbacks.TryGetValue(id, out var callback))
        {
            callback = EventCallback.Factory.Create<PointerEventArgs>(this, () =>
            {
                if (CurrentHold(id) is { } hold)
                {
                    StartDrag(hold);
                }
            });
            holdDownCallbacks[id] = callback;
        }

        return callback;
    }

    private Hold? CurrentHold(Guid id) => Holds.FirstOrDefault(h => h.Id == id);
}
