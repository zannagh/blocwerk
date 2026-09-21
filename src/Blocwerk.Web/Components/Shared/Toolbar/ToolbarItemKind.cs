// <copyright file="ToolbarItemKind.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.Toolbar;

/// <summary>
/// What a toolbar control MEANS. Until this existed every control in the wall editor wore the same
/// <c>.tool-btn</c> chrome, so a mode, a view filter and a one-shot action were indistinguishable —
/// users could not tell which of them they were about to enter, flip or fire.
/// </summary>
public enum ToolbarItemKind
{
    /// <summary>
    /// Exclusive and radio-like: picking one leaves the last, it claims taps on the photo, and it
    /// drives the contextual row. Add / Move / Shape / Paint / Name / Delete / Border are these.
    /// </summary>
    Tool,

    /// <summary>
    /// Independent and persistent: a view filter that stays on across tool changes and never claims a
    /// tap. Show names, show incomplete, show border, show changes.
    /// </summary>
    Toggle,

    /// <summary>
    /// One-shot: fires and returns, never sits "active". Re-detect, auto-align, link panels, the bulk
    /// mark-all actions.
    /// </summary>
    Command,
}
