using System.Collections.Concurrent;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The failure cap for the delete-account step-up, kept deliberately separate from the persisted
/// login lockout.
/// </summary>
/// <remarks>
/// Re-auth used to count its failures into <c>ILoginLockoutService</c>, the same per-user counter
/// that guards signing in. That let anybody who found an unattended, signed-in browser type five
/// wrong passwords on the delete page and lock the owner out of logging in for fifteen minutes —
/// a denial of service handed to somebody who was already refused. The two need different answers:
/// the login cap must be persisted, because an attacker there can clear all client state; this one
/// only has to slow down guessing behind a session that is already authenticated, so an in-process
/// counter is enough and it cannot bleed into the account's ability to sign in.
/// </remarks>
public sealed class AccountReauthThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, AccountReauthFailures> failures = new();

    /// <summary>True while this user has spent their attempts for the current window.</summary>
    public bool IsBlocked(Guid userId)
    {
        return failures.TryGetValue(userId, out var state)
               && state.WindowEndsAt > DateTimeOffset.UtcNow
               && state.Count >= MaxFailures;
    }

    /// <summary>Records one failed step-up.</summary>
    public void RegisterFailure(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        failures.AddOrUpdate(
            userId,
            _ => new AccountReauthFailures(1, now.Add(Window)),
            (_, state) => state.WindowEndsAt <= now
                ? new AccountReauthFailures(1, now.Add(Window))
                : state with { Count = state.Count + 1 });
    }

    /// <summary>Clears the window after a successful step-up.</summary>
    public void Reset(Guid userId)
    {
        failures.TryRemove(userId, out _);
    }
}
