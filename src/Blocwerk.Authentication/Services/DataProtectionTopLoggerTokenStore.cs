using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services.TopLogger;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// Database-backed <see cref="ITopLoggerTokenStore"/> that persists a user's TopLogger token pair
/// encrypted at rest in <see cref="TopLoggerConnection"/>. The tokens are protected with a dedicated
/// DataProtection protector ("blocwerk.toplogger"), whose key ring is the same persisted ring the
/// auth cookies and TOTP secrets use — so tokens saved before a redeploy stay decryptable.
/// <para>
/// Lives in Blocwerk.Authentication (like <see cref="TotpService"/>) because that is where the
/// DataProtection stack is configured and referenced. Blocwerk.Core owns only the interface; the
/// concrete implementation is resolved at runtime, so there is no circular project reference.
/// </para>
/// </summary>
public sealed class DataProtectionTopLoggerTokenStore : ITopLoggerTokenStore
{
    private const string TokenPurpose = "blocwerk.toplogger";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly IDataProtector protector;

    public DataProtectionTopLoggerTokenStore(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        this.dbContextFactory = dbContextFactory;
        protector = dataProtectionProvider.CreateProtector(TokenPurpose);
    }

    /// <inheritdoc />
    public async Task<TopLoggerTokens?> LoadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        TopLoggerConnection? connection = await db.TopLoggerConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (connection is null
            || string.IsNullOrWhiteSpace(connection.AccessTokenProtected)
            || string.IsNullOrWhiteSpace(connection.RefreshTokenProtected))
        {
            return null;
        }

        try
        {
            return new TopLoggerTokens(
                protector.Unprotect(connection.AccessTokenProtected),
                connection.AccessExpiresAt,
                protector.Unprotect(connection.RefreshTokenProtected),
                connection.RefreshExpiresAt);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The key ring rotated or was lost, so the stored ciphertext is no longer decryptable.
            // Treat it as "not connected" — the caller surfaces a reconnect prompt rather than crashing.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(Guid userId, TopLoggerTokens tokens, CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        TopLoggerConnection? connection = await db.TopLoggerConnections
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (connection is null)
        {
            connection = new TopLoggerConnection
            {
                UserId = userId,
                AccessTokenProtected = ProtectOrBlank(tokens.AccessToken),
                RefreshTokenProtected = ProtectOrBlank(tokens.RefreshToken),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.TopLoggerConnections.Add(connection);
        }
        else
        {
            connection.AccessTokenProtected = ProtectOrBlank(tokens.AccessToken);
            connection.RefreshTokenProtected = ProtectOrBlank(tokens.RefreshToken);
        }

        connection.AccessExpiresAt = tokens.AccessExpiresAt;
        connection.RefreshExpiresAt = tokens.RefreshExpiresAt;

        // A successful save means the session is healthy again: drop any stale reconnect prompt.
        connection.NeedsReauth = false;
        connection.LastError = null;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        TopLoggerConnection? connection = await db.TopLoggerConnections
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (connection is null)
        {
            return;
        }

        // Keep the row so the profile still shows a TopLogger link that needs reconnecting; only the
        // token ciphertext is wiped, never leaving decryptable secrets behind for a dead session.
        connection.AccessTokenProtected = string.Empty;
        connection.RefreshTokenProtected = string.Empty;
        connection.NeedsReauth = true;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // Never persist plaintext: a present token is encrypted; a missing one is stored as blank, which
    // LoadAsync reads back as "not connected".
    private string ProtectOrBlank(string? token) =>
        string.IsNullOrWhiteSpace(token) ? string.Empty : protector.Protect(token);
}
