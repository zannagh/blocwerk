// <copyright file="EditPresenceInterop.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.JSInterop;

namespace Blocwerk.Web.State;

/// <summary>
/// The .NET end of <c>edit-presence.js</c>: the browser reports real user input in an open editor, and the page
/// being hidden or shown, onto this circuit's <see cref="CircuitEditActivity"/>. These are the ONLY ways presence
/// (<see cref="EditActivityEntry.LastActivityUtc"/>) moves; see <see cref="EditActivitySignals"/>.
/// </summary>
public sealed class EditPresenceInterop(CircuitEditActivity activity)
{
    /// <summary>Trusted pointer, key or wheel input in the page while an editor is open (throttled by the browser).</summary>
    [JSInvokable]
    public void OnUserInput()
    {
        activity.RecordActivity();
    }

    /// <summary>The page was hidden, frozen or unloaded (<paramref name="visible"/> false), or shown again.</summary>
    [JSInvokable]
    public void OnVisibility(bool visible)
    {
        activity.RecordVisibility(visible);
    }
}
