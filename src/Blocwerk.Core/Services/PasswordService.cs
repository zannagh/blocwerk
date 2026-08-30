using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Identity;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IPasswordService"/> backed by ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/>
/// (PBKDF2 with a per-password random salt). The hasher does not use the user instance in its default
/// configuration, so a throwaway is passed.
/// </summary>
public class PasswordService : IPasswordService
{
    private readonly PasswordHasher<User> hasher = new();

    public string Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password must not be empty.", nameof(password));
        }

        return hasher.HashPassword(null!, password);
    }

    public bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var result = hasher.VerifyHashedPassword(null!, hash, password);

        // A rehash-needed result means the password is correct but stored with older parameters —
        // treat it as success (a re-hash could be persisted opportunistically, not required here).
        return result is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
