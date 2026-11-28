// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.AppleSignIn;

/// <summary>
/// An unlink refused because the Apple ID is the account's only way in: an account made by web Sign in with Apple has
/// no password and no other provider until the user adds one, and unlinking would lock it out for good.
/// </summary>
public sealed class AppleOnlySignInException : InvalidOperationException
{
    /// <summary>The text shown to the user, on the web and in the app.</summary>
    public const string UserMessage =
        "Your Apple ID is the only way into this account. Set a password or link another provider before you unlink it.";

    public AppleOnlySignInException()
        : base(UserMessage)
    {
    }
}
