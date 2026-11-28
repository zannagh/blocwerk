// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// The caller named a wall they are not a member of (as their home wall). An <see cref="InvalidOperationException"/>,
/// as it was before it had its own type, so existing callers still catch it.
/// </summary>
public sealed class NotAWallMemberException : InvalidOperationException
{
    public NotAWallMemberException(string message)
        : base(message)
    {
    }
}
