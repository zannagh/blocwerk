// <copyright file="EditFlushAction.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>What an editor should do with its unsaved state when the pre-deploy flush reaches it (see <see cref="EditorFlushPolicy"/>).</summary>
public enum EditFlushAction
{
    /// <summary>Nothing unsaved (or a save of the user's own is already under way).</summary>
    Nothing,

    /// <summary>Save it on the server the way the user could have.</summary>
    SaveOnServer,

    /// <summary>Keep it in the browser and restore it after the reload.</summary>
    KeepOnDevice,

    /// <summary>It can be neither saved safely nor kept; tell the user.</summary>
    CannotKeep,
}
