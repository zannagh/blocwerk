using Blocwerk.Core.Entities;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// What <see cref="ApiKeyLoginValidator"/> decided. The failure reason is for the server log only;
/// callers answer every failure with the same generic 401.
/// </summary>
/// <param name="User">The user to sign in, or null when the attempt is refused.</param>
/// <param name="Key">The matched key row when the token matched one, for the session and the audit log.</param>
/// <param name="FailureReason">A short, log-only reason when refused.</param>
public sealed record ApiKeyLoginResult(User? User, ApiKey? Key, string? FailureReason)
{
    public bool Succeeded => User is not null && Key is not null;

    /// <summary>The key's row id when known (never the token).</summary>
    public Guid? ApiKeyId => Key?.Id;

    /// <summary>The key's stored display prefix when known (never the full token).</summary>
    public string? KeyPrefix => Key?.Prefix;

    public static ApiKeyLoginResult Success(User user, ApiKey key) => new(user, key, null);

    public static ApiKeyLoginResult Failure(string reason, ApiKey? key = null) => new(null, key, reason);
}
