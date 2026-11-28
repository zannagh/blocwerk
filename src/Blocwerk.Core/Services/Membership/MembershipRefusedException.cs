// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services.Membership;

/// <summary>A removal or a leave the wall's rules do not allow; <see cref="Reason"/> says which rule.</summary>
public sealed class MembershipRefusedException : InvalidOperationException
{
    public MembershipRefusedException(MembershipRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused it.</summary>
    public MembershipRefusal Reason { get; }
}
