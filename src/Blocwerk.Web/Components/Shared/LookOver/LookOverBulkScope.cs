// <copyright file="LookOverBulkScope.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>Which open findings of a kind a bulk answer settles; its button and its question name the scope and the count.</summary>
internal enum LookOverBulkScope
{
    /// <summary>Every open finding of the kind (the "all findings" view).</summary>
    All = 0,

    /// <summary>Those no current boulder uses (the "all findings" view).</summary>
    WithoutBoulders = 1,

    /// <summary>Those a boulder uses: what the "on boulders" view lists.</summary>
    OnBoulders = 2,
}
