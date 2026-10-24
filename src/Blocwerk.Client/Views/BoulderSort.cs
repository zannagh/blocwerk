// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Views;

/// <summary>The boulder list's orders, as the Server's wall view offers them.</summary>
public enum BoulderSort
{
    /// <summary>Newest first.</summary>
    Newest,

    /// <summary>Oldest first.</summary>
    Oldest,

    /// <summary>Easy first; ungraded last.</summary>
    GradeAsc,

    /// <summary>Hard first.</summary>
    GradeDesc,

    /// <summary>Best rated first.</summary>
    Rating,

    /// <summary>Most of the viewer's attempts first (signed in only).</summary>
    MyAttempts,
}
