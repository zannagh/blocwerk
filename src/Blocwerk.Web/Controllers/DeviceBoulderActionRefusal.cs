// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The boulder service's refusals on publish, archive, grade proposals and variants, answered as <c>{ message }</c>:
/// 404 for a boulder or proposal that is not there (or a wall the caller is not on), 403 for a caller without the right,
/// 409 for a boulder in a state that does not allow it, 400 for a variant link that can never be made.
/// </summary>
internal static class DeviceBoulderActionRefusal
{
    private const string ManagePrefix = "Only the creator, a setter, or a wall admin";

    /// <summary>The answer for <paramref name="ex"/>, or null for an unexpected failure.</summary>
    public static IActionResult? From(Exception ex) => ex switch
    {
        UnauthorizedAccessException => DeviceWallRefusal.Refused(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in again."),
        InvalidOperationException { Message: "Boulder not found" or "Proposal not found" or "Only wall members can do this" } =>
            DeviceWallRefusal.Refused(StatusCodes.Status404NotFound, ex.Message),
        InvalidOperationException { Message: BoulderService.VariantPermissionMessage or BoulderService.VariantTargetPermissionMessage } =>
            DeviceWallRefusal.Refused(StatusCodes.Status403Forbidden, ex.Message),
        InvalidOperationException { Message: "Cannot propose a grade for your own boulder" } =>
            DeviceWallRefusal.Refused(StatusCodes.Status403Forbidden, ex.Message),
        InvalidOperationException guard when guard.Message.StartsWith(ManagePrefix, StringComparison.Ordinal) =>
            DeviceWallRefusal.Refused(StatusCodes.Status403Forbidden, ex.Message),
        InvalidOperationException { Message: "Only historic boulders can be archived" or "Select at least one hold before publishing" or BoulderService.ProposalChangedMessage } =>
            DeviceWallRefusal.Refused(StatusCodes.Status409Conflict, ex.Message),
        UserFacingException user => DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, user.Message),
        _ => null,
    };
}
