namespace Blocwerk.Authentication.Services;

/// <summary>One user's failed step-up attempts inside the current window.</summary>
public sealed record AccountReauthFailures(int Count, DateTimeOffset WindowEndsAt);
