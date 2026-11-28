// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Services.DeviceWrites;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// How the companion app's creating writes (gallery, beta video, boulder) answer a replay key that cannot run now, in the
/// envelope of <see cref="OfflineActionRunner"/>.
/// </summary>
internal static class DeviceWriteAnswers
{
    /// <summary>The refusal for <paramref name="claim"/>, or null when the write may run (or is a replay the caller answers).</summary>
    public static IActionResult? Refusal(DeviceWriteClaim claim) => claim.State switch
    {
        DeviceWriteClaimState.Mismatch => OfflineActionRunner.Permanent("clientRequestId was already used for another write."),
        DeviceWriteClaimState.InFlight => OfflineActionRunner.Error(
            StatusCodes.Status409Conflict, "The same write is still running. Retry shortly.", permanent: false),
        _ => null,
    };

    /// <summary>A refusal by HTTP status: a client error is permanent, except the ones a retry can clear.</summary>
    public static ObjectResult Refused(int status, string message) =>
        OfflineActionRunner.Error(status, message, permanent: status is >= 400 and < 500 and not (401 or 408 or 409 or 429));
}
