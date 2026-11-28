// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Controllers;

/// <summary>The outcome of <see cref="WallImageIntake.CommitAsync"/>: the recorded row, or the status and message to answer.</summary>
/// <param name="Image">The gallery row, on success.</param>
/// <param name="ErrorStatus">The HTTP status of a failure.</param>
/// <param name="ErrorMessage">The message of a failure.</param>
internal sealed record WallImageCommit(WallImage? Image, int? ErrorStatus, string? ErrorMessage)
{
    public static WallImageCommit Failed(int status, string message) => new(null, status, message);
}
