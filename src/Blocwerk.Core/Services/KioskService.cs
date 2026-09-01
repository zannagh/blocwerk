using System.Text.RegularExpressions;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <inheritdoc cref="IKioskService"/>
public partial class KioskService : IKioskService
{
    /// <summary>
    /// A decoy hash verified against when no real one exists, so an unknown or non-consenting member
    /// costs the same key derivation as a real wrong PIN and cannot be told apart by response time.
    /// </summary>
    private static readonly Lazy<string> DecoyPinHash =
        new(() => new PasswordService().Hash("0000"), isThreadSafe: true);

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IPasswordService passwordService;
    private readonly ILogger<KioskService> logger;
    private readonly IKioskContext? kioskContext;

    /// <summary>Creates the service.</summary>
    /// <remarks>
    /// <c>kioskContext</c> is optional: hosts with no HTTP layer never register one, which simply
    /// means "never a kiosk". See <see cref="KioskGuard"/> for why the stamped database context is
    /// read as a second source.
    /// </remarks>
    public KioskService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IPasswordService passwordService,
        ILogger<KioskService> logger,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.passwordService = passwordService;
        this.logger = logger;
        this.kioskContext = kioskContext;
    }

    public async Task ConsentAsync(Guid wallId, string? pin)
    {
        var user = await currentUserService.GetCurrentUserAsync();

        // Validate before touching the database, so a malformed PIN never half-applies a consent.
        var hasPin = !string.IsNullOrWhiteSpace(pin);
        if (hasPin && !PinShape().IsMatch(pin!.Trim()))
        {
            throw new InvalidOperationException("A kiosk PIN must be 4 to 8 digits.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        // Consent is granted from your OWN device, never from the tablet. Allowing it here turns a
        // one-time PIN compromise — shoulder-surfed, or guessed — into a permanent one: act as the
        // member once, re-consent with no PIN, and they stay pickable by anybody for ever, with the
        // one credential that protected them silently removed.
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Changing kiosk consent");

        var member = await FindMemberAsync(db, wallId, user.Id);
        if (member is null)
        {
            throw new InvalidOperationException($"User {user.Id} is not a member of wall {wallId}.");
        }

        member.KioskConsentedAt = DateTimeOffset.UtcNow;
        member.KioskPinHash = hasPin ? passwordService.Hash(pin!.Trim()) : null;
        await db.SaveChangesAsync();

        // Never log the PIN itself, nor whether one was set — that is the member's business.
        logger.LogInformation("User {UserId} consented to kiosk use of wall {WallId}", user.Id, wallId);
    }

    public async Task RevokeConsentAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();

        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        // Withdrawing is a de-escalation, so the risk is not the withdrawal itself — it is that the
        // pair "revoke, then consent again without a PIN" is exactly the attack guarded above, and
        // splitting the two would leave a kiosk session able to strip a member's PIN in two steps.
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Changing kiosk consent");

        var member = await FindMemberAsync(db, wallId, user.Id);
        if (member is null || (member.KioskConsentedAt is null && member.KioskPinHash is null))
        {
            return;
        }

        member.KioskConsentedAt = null;
        member.KioskPinHash = null;
        await db.SaveChangesAsync();

        logger.LogInformation("User {UserId} revoked kiosk consent for wall {WallId}", user.Id, wallId);
    }

    public async Task<bool> HasConsentedAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();

        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        return await db.WallMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(m => m.WallId == wallId && m.UserId == user.Id && m.KioskConsentedAt != null);
    }

    /// <inheritdoc/>
    /// <remarks>Authorisation is the caller's job; see <see cref="IKioskService"/>.</remarks>
    public async Task<IReadOnlyList<KioskUserInfo>> GetConsentingUsersAsync(Guid wallId)
    {
        // No current-user resolution here on purpose: the kiosk is anonymous while the picker shows.
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var members = await db.WallMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(m => m.User)
            .Where(m => m.WallId == wallId && m.KioskConsentedAt != null)
            .ToListAsync();

        // Name and HasAvatar are computed CLR properties, so the projection happens client-side.
        return members
            .Select(m => new KioskUserInfo(
                m.UserId,
                m.User.Name,
                m.User.HasAvatar,
                m.KioskPinHash is not null))
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <inheritdoc/>
    /// <remarks>Authorisation is the caller's job; see <see cref="IKioskService"/>.</remarks>
    public async Task<bool> VerifyPinAsync(Guid wallId, Guid userId, string? pin)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var member = await db.WallMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.WallId == wallId && m.UserId == userId);

        var consented = member?.KioskConsentedAt is not null;
        var storedHash = consented ? member!.KioskPinHash : null;
        var supplied = pin?.Trim();

        if (storedHash is null)
        {
            // Burn an equivalent key derivation whenever a PIN was offered, so "no such member",
            // "never consented" and "wrong PIN" are indistinguishable by timing.
            if (!string.IsNullOrEmpty(supplied))
            {
                passwordService.Verify(DecoyPinHash.Value, supplied);
                return false;
            }

            return consented;
        }

        return !string.IsNullOrEmpty(supplied) && passwordService.Verify(storedHash, supplied);
    }

    private static Task<WallMember?> FindMemberAsync(BlocwerkDbContext db, Guid wallId, Guid userId)
    {
        return db.WallMembers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.WallId == wallId && m.UserId == userId);
    }

    [GeneratedRegex(@"^\d{4,8}$")]
    private static partial Regex PinShape();
}
