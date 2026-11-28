// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The companion app's wall admin routes answer as the web decides: a wall the caller is not a member of is a bare 404
/// (as an unknown wall), a member without the role is a 403 <c>{ message }</c>, and the services' own refusals map onto
/// 400, 403, 404 and 409.
/// </summary>
internal static class DeviceWallRefusal
{
    /// <summary>The message of a 403 for a member who is not an admin.</summary>
    public const string AdminsOnly = "Only the wall's admins can do this.";

    /// <summary>The message of a 403 for an admin who is not the owner.</summary>
    public const string OwnerOnly = "Only the wall's owner can do this.";

    /// <summary>The refusal unless <paramref name="caller"/> is an admin (or the owner) of the wall; null when they are.</summary>
    public static IActionResult? UnlessAdmin(WallCaller? caller) => caller switch
    {
        null or { IsMember: false } => new NotFoundResult(),
        { IsAdmin: false } => Forbidden(AdminsOnly),
        _ => null,
    };

    /// <summary>The refusal unless <paramref name="caller"/> owns the wall; null when they do.</summary>
    public static IActionResult? UnlessOwner(WallCaller? caller) => caller switch
    {
        null or { IsMember: false } => new NotFoundResult(),
        { IsOwner: false } => Forbidden(OwnerOnly),
        _ => null,
    };

    /// <summary>A refusal as <c>{ message }</c> with <paramref name="status"/>.</summary>
    public static ObjectResult Refused(int status, string message) =>
        new(new ApiErrorResponse(message)) { StatusCode = status };

    /// <summary>The answer for a service's domain refusal, or null for anything else (an unexpected failure).</summary>
    public static IActionResult? FromService(Exception ex) => ex switch
    {
        UnauthorizedAccessException => Forbidden(AdminsOnly),
        InvalidOperationException { Message: "Wall not found" } => new NotFoundResult(),
        InvalidOperationException { Message: "Member not found" } => Refused(StatusCodes.Status404NotFound, "Member not found"),
        InvalidOperationException { Message: WallService.OwnerRoleMessage } => Forbidden(WallService.OwnerRoleMessage),
        InvalidOperationException { Message: WallService.FirstPhotoTakenMessage } => Refused(StatusCodes.Status409Conflict, ex.Message),
        InvalidOperationException { Message: StoredPhotoSanitizer.DamagedMessage } => Refused(StatusCodes.Status400BadRequest, ex.Message),
        UserFacingException user => Refused(StatusCodes.Status400BadRequest, user.Message),
        _ => null,
    };

    private static ObjectResult Forbidden(string message) => Refused(StatusCodes.Status403Forbidden, message);
}
