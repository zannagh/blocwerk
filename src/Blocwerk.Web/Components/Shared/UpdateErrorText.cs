// <copyright file="UpdateErrorText.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What the wall-update screens say when a save fails: a refusal written for the user (<see cref="UserFacingException"/>,
/// which includes "this update was replaced") is shown as it is; anything else may be EF Core or framework internals, so
/// it is logged and the screen shows the generic message.
/// </summary>
internal static class UpdateErrorText
{
    /// <summary>The text to show for <paramref name="ex"/>; logs it first when it is not written for the user.</summary>
    public static string Describe(Exception ex, ILogger logger, string what)
    {
        if (ex is UserFacingException)
        {
            return ex.Message;
        }

        logger.LogWarning(ex, "Wall update: {What} failed", what);
        return UserFacingException.GenericMessage;
    }
}
