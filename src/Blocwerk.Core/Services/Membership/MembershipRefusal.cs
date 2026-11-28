// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services.Membership;

/// <summary>Why a member could not be removed or could not leave.</summary>
public enum MembershipRefusal
{
    /// <summary>The person named is not a member of the wall.</summary>
    NotAMember = 0,

    /// <summary>The wall's owner can neither be removed nor leave.</summary>
    Owner = 1,

    /// <summary>An admin removing themselves: that is leaving, not removing.</summary>
    Self = 2,
}
