// <copyright file="EditorFlushPolicy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// Per editor, what the pre-deploy flush does with unsaved state. One place, pure, so the choices are tested and the
/// components only carry them out.
/// </summary>
/// <remarks>
/// The rule behind every row: save on the server only what the user could have saved with one tap and what stays a
/// draft (wall members can see drafts, but a draft notifies nobody and is not loggable); keep on the device whatever
/// would otherwise publish, rename or create something on their behalf; and say so when neither is possible. Never
/// discard silently.
/// </remarks>
public static class EditorFlushPolicy
{
    /// <summary>
    /// How much of the wall editor's batch a flush saves. THE product switch for deletions and "Mark as changed":
    /// <see cref="WallFlushScope.Everything"/> saves them like the button; <see cref="WallFlushScope.AddsAndMovesOnly"/>
    /// keeps them on the device for the user to confirm.
    /// </summary>
    public static readonly WallFlushScope WallFlush = WallFlushScope.Everything;

    /// <summary>
    /// Boulder create: a signed-in setter (including a kiosk acting as a member, which is the member by its cookie)
    /// gets a draft; an anonymous kiosk create is kept on the tablet, because a draft owned by the Ghost is visible to
    /// nobody.
    /// </summary>
    public static EditFlushAction BoulderCreate(bool dirty, bool submitting, bool anonymous) =>
        !dirty || submitting ? EditFlushAction.Nothing
        : anonymous ? EditFlushAction.KeepOnDevice
        : EditFlushAction.SaveOnServer;

    /// <summary>
    /// A restored create copy: its id is reused ONLY for a copy a deploy flush kept that no boulder carries yet. A copy
    /// made while the page was hidden may carry an id a boulder took since (the offline queue, another tab), and
    /// creating "again" under it would silently return that boulder instead of the new one.
    /// </summary>
    public static RestoredCreateId RestoreCreate(bool fromDeploy, bool boulderExists) =>
        boulderExists ? RestoredCreateId.AlreadySaved
        : fromDeploy ? RestoredCreateId.Reuse
        : RestoredCreateId.Fresh;

    /// <summary>
    /// Boulder revise: there is no draft revision of a live or historic boulder (a revise rewrites it in place and
    /// brings a historic one back live), so only a draft boulder is saved; anything else is kept on the device.
    /// </summary>
    public static EditFlushAction BoulderRevise(bool dirty, bool submitting, bool isDraft) =>
        !dirty || submitting ? EditFlushAction.Nothing
        : isDraft ? EditFlushAction.SaveOnServer
        : EditFlushAction.KeepOnDevice;

    /// <summary>The boulder page's inline rename/regrade and grade proposal act on a live boulder at once: kept, never sent.</summary>
    public static EditFlushAction InlineBoulderEdit(bool dirty) =>
        dirty ? EditFlushAction.KeepOnDevice : EditFlushAction.Nothing;

    /// <summary>A new wall is never created on the user's behalf: the form is kept (the photo cannot be).</summary>
    public static EditFlushAction WallCreate(bool dirty, bool submitting) =>
        !dirty || submitting ? EditFlushAction.Nothing : EditFlushAction.KeepOnDevice;

    /// <summary>
    /// Wall edit mode: the pending hold batch is saved as "Save Changes" would (in the scope of <see cref="WallFlush"/>),
    /// unless the user's own save is already in flight (<paramref name="submitting"/>), which carries it. A manual
    /// alignment in progress is not confirmed for the user ("Confirm &amp; Update Wall" re-projects the whole wall).
    /// </summary>
    public static EditFlushAction WallEditor(bool manualAlignment, bool alignmentWork, bool canEdit, bool pending, bool submitting) =>
        manualAlignment ? (alignmentWork ? EditFlushAction.CannotKeep : EditFlushAction.Nothing)
        : canEdit && pending && !submitting ? EditFlushAction.SaveOnServer
        : EditFlushAction.Nothing;
}
