using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Abstractions;

/// <summary>Why a local (email + password) signup did or didn't create a user.</summary>
public enum LocalUserCreateStatus
{
    /// <summary>The account was created.</summary>
    Created = 0,

    /// <summary>The username, password or email failed validation; nothing was created.</summary>
    Invalid = 1,

    /// <summary>The chosen username is already in use (case-insensitively); nothing was created.</summary>
    UsernameTaken = 2,

    /// <summary>The email address is already in use by another account; nothing was created.</summary>
    EmailTaken = 3,
}

/// <summary>
/// The outcome of <see cref="IPasswordLoginService.CreateLocalUserAsync"/>. <see cref="User"/> is the
/// created account on <see cref="LocalUserCreateStatus.Created"/> and null on every failure.
/// </summary>
public record LocalUserCreateResult(LocalUserCreateStatus Status, User? User = null)
{
    /// <summary>True when a user was created.</summary>
    public bool Success => Status == LocalUserCreateStatus.Created;
}
