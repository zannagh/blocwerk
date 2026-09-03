namespace Blocwerk.Authentication.Services;

/// <summary>One issued step-up proof: whose it is, and when it stops counting.</summary>
public sealed record AccountReauthTicket(Guid UserId, DateTimeOffset ExpiresAt);
