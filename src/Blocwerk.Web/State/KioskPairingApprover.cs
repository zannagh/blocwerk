using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.State;

/// <summary>
/// The one routine both approval entry points go through: the QR/scan page and the wall-settings
/// card. Everything security-relevant about approving a pairing lives here, once.
/// </summary>
/// <remarks>
/// Scoped, because it resolves the ACTING USER from the ambient session rather than taking a user
/// id from its caller. That is the point: a page cannot approve on behalf of somebody else by
/// passing the wrong id, and there is no parameter to get wrong.
/// <para>
/// <b>The admin check is not this class's own.</b> <see cref="IApiKeyService.CreateKioskKeyAsync"/>
/// runs <c>WallAdminGuard.EnsureWallAdminAsync</c> inside the service, against the database, at the
/// moment of minting — so the authorisation and the credential are issued together and cannot drift
/// apart. The wall list this class offers the UI is convenience only; a caller that posts a wall id
/// it has no business with reaches the same guard and gets nothing. The DI-registered
/// <c>KioskGuardedApiKeyService</c> sits in front of it and additionally refuses when the APPROVING
/// session is itself a kiosk, which is what stops a paired tablet from pairing more tablets.
/// </para>
/// </remarks>
public sealed class KioskPairingApprover
{
    private readonly IApiKeyService apiKeyService;
    private readonly ICurrentUserService currentUserService;
    private readonly IWallService wallService;
    private readonly KioskPairingRegistry pairings;
    private readonly ILogger<KioskPairingApprover> logger;

    public KioskPairingApprover(
        IApiKeyService apiKeyService,
        ICurrentUserService currentUserService,
        IWallService wallService,
        KioskPairingRegistry pairings,
        ILogger<KioskPairingApprover> logger)
    {
        this.apiKeyService = apiKeyService;
        this.currentUserService = currentUserService;
        this.wallService = wallService;
        this.pairings = pairings;
        this.logger = logger;
    }

    /// <summary>
    /// The signed-in user's id, or null when nobody is signed in. Only ever used to KEY a throttle;
    /// nothing is authorised on it.
    /// </summary>
    public async Task<Guid?> TryGetActingUserIdAsync()
    {
        try
        {
            return (await currentUserService.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The walls the signed-in user administers, for the picker. Owner or <see cref="WallRole.Admin"/>,
    /// matching what <c>WallAdminGuard</c> will accept when the key is actually minted.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Nobody is signed in.</exception>
    public async Task<IReadOnlyList<Wall>> GetAdministeredWallsAsync()
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var walls = await wallService.GetMyWallsAsync();

        return walls
            .Where(w => w.OwnerId == user.Id
                        || w.Members.Any(m => m.UserId == user.Id && m.Role == WallRole.Admin))
            .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Approves a pending pairing against a wall: mints that wall its own kiosk key, throws the
    /// plaintext token away, and hands the pairing the key's id.
    /// </summary>
    /// <remarks>
    /// <b>The plaintext token never leaves this method.</b> The tablet is registered with the device
    /// cookie, which needs only the key's id and the wall — so the one moment the token exists, it is
    /// discarded. There is nothing for the tablet to store, nothing to show on a screen bolted to a
    /// public wall, and nothing for a bystander to photograph.
    /// <para>
    /// Each pairing mints its OWN key, named for the day it was paired, so a tablet that goes
    /// missing is revoked on its own without unregistering every other tablet on the wall.
    /// </para>
    /// </remarks>
    public async Task<KioskPairingApprovalResult> ApproveAsync(
        Guid pairingId,
        Guid wallId,
        CancellationToken ct = default)
    {
        if (wallId == Guid.Empty)
        {
            return KioskPairingApprovalResult.NoWall;
        }

        // Cheap pre-check so the overwhelmingly common failures — a code that lapsed while the admin
        // was reading the page — never mint anything at all.
        if (pairings.Find(pairingId) is not { Status: KioskPairingStatus.Pending })
        {
            return KioskPairingApprovalResult.PairingUnavailable;
        }

        Guid actingUserId;
        try
        {
            actingUserId = (await currentUserService.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return KioskPairingApprovalResult.NotAuthorised;
        }

        Guid mintedKeyId;
        try
        {
            var name = $"Kiosk tablet paired {DateTimeOffset.UtcNow:yyyy-MM-dd}";

            // The authoritative wall-admin check runs inside this call, against the database.
            var (key, _) = await apiKeyService.CreateKioskKeyAsync(wallId, actingUserId, name, expiresAt: null, ct);
            mintedKeyId = key.Id;
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogWarning(
                "User {UserId} tried to approve kiosk pairing {PairingId} for wall {WallId} they do not administer",
                actingUserId,
                pairingId,
                wallId);
            return KioskPairingApprovalResult.NotAuthorised;
        }
        catch (KioskRestrictedException)
        {
            // The APPROVING session is itself a kiosk tablet (KioskGuardedApiKeyService), or the wall
            // is not the one this tablet is registered to (WallAdminGuard). Deliberately not an
            // UnauthorizedAccessException upstream, and it must not escape either: both callers are
            // interactive circuits, so an uncaught throw here kills a wall admin's page rather than
            // telling them no.
            logger.LogWarning(
                "Kiosk pairing {PairingId} for wall {WallId} was refused: approving from a kiosk session is not allowed",
                pairingId,
                wallId);
            return KioskPairingApprovalResult.NotAuthorised;
        }

        if (!pairings.TryApprove(pairingId, wallId, mintedKeyId))
        {
            // It lapsed or was approved by somebody else in the moments since the pre-check. The key
            // is already real, so revoke it rather than leaving a live kiosk credential attached to
            // nothing — nobody would ever think to go looking for it.
            logger.LogInformation(
                "Kiosk pairing {PairingId} was gone by the time key {ApiKeyId} was minted; revoking it",
                pairingId,
                mintedKeyId);

            try
            {
                await apiKeyService.RevokeAsync(mintedKeyId, actingUserId, ct);
            }
            catch (Exception ex)
            {
                // The revoke is the ONLY thing standing between this race and a live kiosk key for a
                // real wall attached to no pairing, which nobody would ever think to go looking for.
                // If it fails, the key id has to reach somebody, so it is logged at Error with the
                // wall and the acting user — everything needed to revoke it by hand from the API key
                // panel. Swallowed rather than rethrown: the caller is an interactive circuit, and
                // killing the admin's page would tell them less than the generic failure does.
                logger.LogError(
                    ex,
                    "ORPHANED KIOSK KEY: key {ApiKeyId} was minted for wall {WallId} by user {UserId} for "
                    + "pairing {PairingId}, the pairing was gone, and revoking it FAILED. This key is live "
                    + "and attached to no device; revoke it by hand.",
                    mintedKeyId,
                    wallId,
                    actingUserId,
                    pairingId);
            }

            return KioskPairingApprovalResult.PairingUnavailable;
        }

        logger.LogInformation(
            "Kiosk pairing {PairingId} approved for wall {WallId} by user {UserId} with key {ApiKeyId}",
            pairingId,
            wallId,
            actingUserId,
            mintedKeyId);

        return KioskPairingApprovalResult.Approved;
    }
}

/// <summary>How an approval attempt ended.</summary>
public enum KioskPairingApprovalResult
{
    /// <summary>The wall has its key and the tablet has been told.</summary>
    Approved = 0,

    /// <summary>Unknown, expired, or already approved. One value on purpose: the UI must not say which.</summary>
    PairingUnavailable = 1,

    /// <summary>Nobody is signed in, or the signed-in user does not administer the chosen wall.</summary>
    NotAuthorised = 2,

    /// <summary>No wall was chosen. A pairing never carries one, so it must come from the form.</summary>
    NoWall = 3,
}
