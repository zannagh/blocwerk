// <copyright file="UserFacingException.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// A refusal whose message was written for the person using the app ("switch on printed markers first",
/// "the video is too large") and may be shown to them verbatim. API endpoints map ONLY this type to its
/// message; any other <see cref="InvalidOperationException"/> may come from EF Core or the framework and
/// can carry internals, so it is logged and answered with a generic text instead.
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so existing callers that catch that (the Blazor
/// UI shows its message in a toast) keep working unchanged.
/// </para>
/// </summary>
public class UserFacingException : InvalidOperationException
{
    /// <summary>The text an endpoint answers with when the exception is not a <see cref="UserFacingException"/>.</summary>
    public const string GenericMessage = "The request could not be completed.";

    public UserFacingException(string message)
        : base(message)
    {
    }

    public UserFacingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
