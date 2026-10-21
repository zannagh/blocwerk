// <copyright file="EditFlushOutcome.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>What one open editor did when asked to persist its unsaved state before a deploy.</summary>
public enum EditFlushOutcome
{
    /// <summary>The editor had nothing unsaved.</summary>
    NothingToSave,

    /// <summary>Saved on the server the way the user could have saved it (a draft boulder, a draft's revision, the wall batch).</summary>
    SavedOnServer,

    /// <summary>Could not be saved safely on the server, so it was kept in the browser and restores after the reload.</summary>
    KeptOnDevice,

    /// <summary>Neither worked (the save failed and the browser was unreachable, or the state cannot be kept). The user is told.</summary>
    NotSaved,
}
