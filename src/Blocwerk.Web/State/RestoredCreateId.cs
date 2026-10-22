// <copyright file="RestoredCreateId.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>What a restored boulder-create copy does with the client id it carries (see <see cref="EditorFlushPolicy.RestoreCreate"/>).</summary>
public enum RestoredCreateId
{
    /// <summary>A boulder with that id already exists: the copy is saved already and is not restored as a form.</summary>
    AlreadySaved,

    /// <summary>The same unsaved create a deploy flush kept: keep its id, so saving it stays idempotent.</summary>
    Reuse,

    /// <summary>A copy made while the page was hidden: restore the form under a fresh id.</summary>
    Fresh,
}
