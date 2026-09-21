using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public interface IBoulderService
{
    /// <summary>
    /// Creates a boulder. <paramref name="handsFollowFeet"/> and <paramref name="footColorOnly"/>
    /// are the boulder's foothold rules; a <see cref="HoldUsage.FootOnly"/> mark is coerced
    /// to <see cref="HoldUsage.HandAndFoot"/> while hands follow feet.
    /// <para>
    /// Pass a client-minted <paramref name="id"/> when the call may be replayed from an offline
    /// queue: creation becomes an idempotent upsert on that id, so a second call with the same id
    /// (from the same creator) returns the boulder already stored instead of inserting a duplicate.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <paramref name="setterUserIds"/> attributes the boulder to one or more setters (co-setters),
    /// distinct from the creator. Each id is validated as a member of the wall; non-members are
    /// skipped. Null or empty leaves the boulder without an explicit setter.
    /// </remarks>
    Task<Boulder> CreateBoulderAsync(
        Guid wallId,
        string name,
        string? grade,
        List<BoulderHoldInput> holds,
        bool isDraft = false,
        bool kickboardFootholdsOn = true,
        bool handsFollowFeet = true,
        string? footColorOnly = null,
        Guid? id = null,
        bool noMatch = false,
        IReadOnlyList<Guid>? setterUserIds = null);

    /// <summary>
    /// Whether THIS session — a registered kiosk tablet with nobody signed in — satisfies every
    /// PERMISSION condition in <see cref="KioskAnonymousSetting"/> for creating a boulder on
    /// <paramref name="wallId"/>: a live kiosk key aimed at this very wall, the wall's opt-in, and
    /// that key's own flag. Exposed so a page can decide whether to offer the button without
    /// disagreeing with the server about who is allowed.
    /// </summary>
    /// <remarks>
    /// It is NOT the whole of what <see cref="CreateBoulderAsync"/> asks: the create ALSO consults
    /// the volume throttle, which this deliberately does not touch — a predicate a page may call on
    /// every render must not consume or advance throttle state. So a true here means "permitted",
    /// not "will succeed": a create can still be refused because the tablet is over its budget.
    /// </remarks>
    Task<bool> CanCreateAnonymouslyAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// Makes a draft visible to everyone on the wall.
    /// </summary>
    Task<Boulder> PublishBoulderAsync(Guid boulderId);

    Task<Boulder?> GetBoulderAsync(Guid boulderId);

    Task<Boulder?> GetBoulderByShareTokenAsync(Guid boulderId, string shareToken);

    /// <summary>
    /// The generation the boulder should be SHOWN at in the historic ("Then") view, or null when it
    /// has no older state worth offering — i.e. the resolved generation is the wall's current one.
    /// </summary>
    /// <remarks>
    /// Per hold: its ANCESTOR's generation when cross-generation lineage records one, else the hold's
    /// own generation; the boulder's value is the MAX over its holds. The ancestor hop is what makes
    /// this correct — a hold CARRIED through a wall update gets a fresh row at the current generation,
    /// so the boulder's own hold generations reveal nothing about it having changed, and gating on
    /// them alone hid the toggle from exactly the boulders flagged because a hold changed. The hop is
    /// ONE generation back (the immediate predecessor), not transitively to the origin: the question
    /// is "how did it look before the change that flagged it".
    /// <para>
    /// Pass <paramref name="shareToken"/> to resolve it on the anonymous share-link path; that read
    /// is gated by the wall's share token exactly like <see cref="GetBoulderByShareTokenAsync"/>
    /// instead of by viewer membership, which an anonymous viewer has none of.
    /// </para>
    /// </remarks>
    Task<int?> GetHistoricGenerationAsync(Guid boulderId, string? shareToken = null, CancellationToken ct = default);

    /// <summary>
    /// The boulder's holds as they existed at <paramref name="generation"/>, for drawing it on that
    /// generation's photos. Null when the boulder cannot be read at all (unknown id, or no access) —
    /// deliberately distinct from an EMPTY list, which means "read fine, nothing of this boulder
    /// existed back then" and lets the caller tell a failure apart from a genuinely empty map.
    /// </summary>
    /// <remarks>
    /// Each hold is followed BACKWARDS through <see cref="Entities.HoldGenerationLink"/> (successor →
    /// predecessor) until a row at <paramref name="generation"/> is reached; a hold already at that
    /// generation maps to itself with no lineage needed. A hold whose chain never reaches the target
    /// did not exist then and is left out. The boulder's own <see cref="HoldType"/> /
    /// <see cref="HoldUsage"/> marks ride along, because they belong to the boulder rather than to
    /// any one generation of the wall.
    /// <para>
    /// This is what makes the historic view show a CARRIED boulder at all: such a boulder's holds are
    /// fresh rows at today's generation pointing at today's panels, so drawing them against the old
    /// generation's panels matches nothing and the overlay comes out empty.
    /// </para>
    /// <para>
    /// Pass <paramref name="shareToken"/> to resolve it on the anonymous share-link path, gated by
    /// the wall's share token exactly like <see cref="GetBoulderByShareTokenAsync"/> — drafts
    /// included: that path never returns an unpublished boulder, whoever holds the token.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<BoulderHoldAtGeneration>?> GetBoulderHoldsAtGenerationAsync(
        Guid boulderId, int generation, string? shareToken = null, CancellationToken ct = default);

    Task<List<Boulder>> GetBouldersForWallAsync(Guid wallId, bool includeArchived = false);

    /// <summary>
    /// Which boulders currently use each hold on the wall, keyed by hold id. Holds that
    /// no boulder uses are absent from the map. Drafts are included — they are visible to
    /// every wall member — and flagged via <see cref="HoldUsageRef.IsDraft"/>.
    /// </summary>
    Task<Dictionary<Guid, List<HoldUsageRef>>> GetHoldUsageAsync(Guid wallId);

    /// <summary>
    /// Updates a boulder. Null rule arguments leave the stored rule untouched; pass an
    /// empty string for <paramref name="footColorOnly"/> to clear the foot color rule.
    /// </summary>
    Task<Boulder> UpdateBoulderAsync(
        Guid boulderId,
        string name,
        string? grade,
        List<BoulderHoldInput>? holds = null,
        bool? kickboardFootholdsOn = null,
        bool? handsFollowFeet = null,
        string? footColorOnly = null);

    /// <summary>
    /// Remaps a historic or draft boulder onto the current hold model, optionally renaming
    /// and regrading it. Attempts, comments and grade proposals are preserved. Null rule
    /// arguments leave the stored rule untouched; pass an empty string for
    /// <paramref name="footColorOnly"/> to clear the foot color rule.
    /// <para>
    /// When <paramref name="setterUserIds"/> is non-null it REPLACES the boulder's setter set
    /// (each id validated as a wall member; non-members skipped). Null leaves the setters untouched.
    /// </para>
    /// </summary>
    Task<Boulder> ReviseBoulderAsync(
        Guid boulderId,
        List<BoulderHoldInput> updatedHolds,
        string? name = null,
        string? grade = null,
        bool? kickboardFootholdsOn = null,
        bool? handsFollowFeet = null,
        string? footColorOnly = null,
        bool noMatch = false,
        IReadOnlyList<Guid>? setterUserIds = null);

    /// <summary>
    /// Renames and/or regrades a boulder in place, without touching its holds. Only the
    /// creator may do this, and it works on a live boulder (unlike <see cref="ReviseBoulderAsync"/>,
    /// which is for historic/draft remaps). Pass null or empty <paramref name="grade"/> to clear it.
    /// </summary>
    Task<Boulder> RenameBoulderAsync(Guid boulderId, string name, string? grade);

    /// <summary>
    /// Permanently removes a boulder. Only its creator, one of its setters, or an admin of its
    /// wall may do so; anybody else is refused with an <see cref="InvalidOperationException"/>.
    /// </summary>
    Task DeleteBoulderAsync(Guid boulderId);

    /// <summary>
    /// Moves one of the creator's own historic boulders into the archive. Only the
    /// creator may archive, and only a historic boulder can be archived.
    /// </summary>
    Task ArchiveBoulderAsync(Guid boulderId);

    Task UnarchiveBoulderAsync(Guid boulderId);

    Task<GradeProposal> ProposeGradeAsync(Guid boulderId, string proposedGrade);

    Task<GradeProposal?> GetActiveProposalAsync(Guid boulderId);

    Task AcceptGradeProposalAsync(Guid proposalId);

    Task RejectGradeProposalAsync(Guid proposalId);
}

public record BoulderHoldInput(Guid HoldId, HoldType Type = HoldType.Normal, HoldUsage Usage = HoldUsage.HandAndFoot);

/// <summary>
/// One boulder's use of a hold, for the wall's "is this hold in use?" tool.
/// </summary>
public record HoldUsageRef(
    Guid BoulderId,
    string Name,
    string? Grade,
    HoldType Type,
    HoldUsage Usage,
    bool IsDraft,
    bool IsHistoric);

public class BoulderService : IBoulderService
{
    /// <summary>
    /// Thrown by <see cref="ReviseBoulderAsync"/> when another climber has already sent the boulder
    /// so only its name and grade may change. Exposed as a const so the offline controller can list
    /// it among its permanent (non-retryable) rejections without the two strings drifting apart.
    /// </summary>
    public const string SentByOthersRevisionMessage =
        "This boulder has already been sent by others; only its name and grade can be changed";

    /// <summary>
    /// Thrown by <see cref="ReviseBoulderAsync"/> when the acting user is neither the boulder's
    /// creator, one of its setters, nor a wall admin. Exposed as a const so the offline controller
    /// can list it among its permanent (non-retryable) rejections without the strings drifting apart.
    /// </summary>
    public const string CreatorOrAdminRevisionMessage =
        "Only the creator, a setter, or a wall admin can revise a boulder";

    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<BoulderService> _logger;
    private readonly IKioskContext? _kioskContext;
    private readonly IKioskKeyValidator? _kioskKeyValidator;
    private readonly KioskAnonymousSettingThrottle? _kioskThrottle;
    private readonly IPushNotificationService? _pushNotificationService;

    /// <summary>Creates the service.</summary>
    /// <remarks>
    /// <c>kioskContext</c> is optional, like <c>WallService</c>'s: hosts without an HTTP layer
    /// (tests, tooling) never register one, which simply means "never a kiosk". It LOOSENS reads for
    /// an anonymous kiosk browsing its own wall, and — only together with the two collaborators
    /// below — the ONE anonymous write in the app: setting a boulder at an unattended tablet.
    /// <para>
    /// <c>kioskKeyValidator</c> and <c>kioskThrottle</c> are optional for the same hosting reason and
    /// fail CLOSED: without the validator a kiosk key cannot be re-checked against the database, and
    /// without the throttle an unattended write surface would be unbounded, so either one missing
    /// means no anonymous create at all. Nothing about the ORDINARY signed-in path changes when they
    /// are absent.
    /// </para>
    /// </remarks>
    public BoulderService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IActivityLogService activityLogService,
        ILogger<BoulderService> logger,
        IKioskContext? kioskContext = null,
        IKioskKeyValidator? kioskKeyValidator = null,
        KioskAnonymousSettingThrottle? kioskThrottle = null,
        IPushNotificationService? pushNotificationService = null)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _activityLogService = activityLogService;
        _logger = logger;
        _kioskContext = kioskContext;
        _kioskKeyValidator = kioskKeyValidator;
        _kioskThrottle = kioskThrottle;
        _pushNotificationService = pushNotificationService;
    }

    public async Task<Boulder> CreateBoulderAsync(
        Guid wallId,
        string name,
        string? grade,
        List<BoulderHoldInput> holds,
        bool isDraft = false,
        bool kickboardFootholdsOn = true,
        bool handsFollowFeet = true,
        string? footColorOnly = null,
        Guid? id = null,
        bool noMatch = false,
        IReadOnlyList<Guid>? setterUserIds = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Create", wallId);
        try
        {
            holds = EnforceHandsFollowFeet(holds, handsFollowFeet);

            // Resolve WHO is setting this before anything else. Almost always a signed-in user; the
            // one exception is an unattended kiosk tablet, which is credited to the Ghost system row
            // and has to earn that with the five checks in KioskAnonymousSetting.
            User? user = null;
            var anonymousKiosk = false;
            try
            {
                user = await _currentUserService.GetCurrentUserAsync();
            }
            catch (UnauthorizedAccessException)
            {
                anonymousKiosk = true;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            // Guid.Empty opens the membership half of the wall query filter, exactly as it does for
            // an anonymous kiosk READ (see GetBoulderAsync). It is safe here for the same reason and
            // only for that reason: every context this session creates is stamped with the tablet's
            // own wall id, and the grant below has already pinned wallId to that same wall.
            db.CurrentUserId = anonymousKiosk ? Guid.Empty : user!.Id;

            if (anonymousKiosk)
            {
                await EnsureAnonymousKioskCreateAllowedAsync(db, wallId);
            }

            var creatorId = anonymousKiosk ? GhostUser.Id : user!.Id;

            // Idempotent replay: a queued offline create that reaches the server twice must not
            // insert a second boulder. The client mints the id, so an existing row under that id is
            // the earlier apply of this very create; return it unchanged rather than re-inserting.
            if (id.HasValue)
            {
                var existing = await db.Boulders
                    .Include(b => b.BoulderHolds)
                    .FirstOrDefaultAsync(b => b.Id == id.Value);

                if (existing != null)
                {
                    if (existing.CreatedByUserId != creatorId)
                    {
                        // A client id colliding with another user's boulder is astronomically
                        // unlikely; surface it as not-found so the queue drops it permanently.
                        _logger.LogWarning(
                            "Create replay rejected: boulder {BoulderId} belongs to {OwnerUserId}, not caller {UserId}",
                            id.Value, existing.CreatedByUserId, creatorId);
                        throw new InvalidOperationException("Boulder not found");
                    }

                    return existing;
                }
            }

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Create boulder failed: wall {WallId} not found for user {UserId}", wallId, creatorId);
                throw new InvalidOperationException("Wall not found");
            }

            var boulder = new Boulder
            {
                Id = id ?? Guid.NewGuid(),
                WallId = wallId,
                Name = name.Trim(),
                Grade = grade,
                CreatedByUserId = creatorId,
                Generation = wall.CurrentGeneration,
                KickboardFootholdsOn = kickboardFootholdsOn,
                HandsFollowFeet = handsFollowFeet,
                FootColorOnly = NormalizeFootColor(footColorOnly),
                NoMatch = noMatch,
                IsDraft = isDraft,
                PublishedAt = isDraft ? null : DateTimeOffset.UtcNow,
            };

            db.Boulders.Add(boulder);

            foreach (var h in holds)
            {
                db.BoulderHolds.Add(new BoulderHold
                {
                    BoulderId = boulder.Id,
                    HoldId = h.HoldId,
                    Type = h.Type,
                    Usage = h.Usage,
                });
            }

            if (anonymousKiosk)
            {
                // An id in an anonymous request is pure user input, so it is validated against the
                // kiosk CONSENT allow-list and refused outright rather than quietly dropped.
                foreach (var setterId in await KioskAnonymousSetting.ValidateSettersAsync(db, wallId, setterUserIds))
                {
                    db.BoulderSetters.Add(new BoulderSetter { BoulderId = boulder.Id, UserId = setterId });
                }
            }
            else
            {
                await AddSettersAsync(db, boulder.Id, wallId, setterUserIds);
            }

            await db.SaveChangesAsync();

            // The write landed, so now it costs budget. Everything that could still have refused it
            // — the setter allow-list, the wall lookup, the save itself — is behind us.
            if (anonymousKiosk)
            {
                RecordAnonymousKioskCreate(wallId);
            }

            BlocwerkMetrics.RecordBoulderCreated(wallId, isDraft);
            _logger.LogInformation(
                "Boulder {BoulderId} created on wall {WallId} by {UserId} (isDraft={IsDraft}, holds={HoldCount}, anonymousKiosk={AnonymousKiosk})",
                boulder.Id, wallId, creatorId, isDraft, holds.Count, anonymousKiosk);

            // Drafts stay out of the activity feed until they are published.
            if (!isDraft)
            {
                if (anonymousKiosk)
                {
                    await _activityLogService.LogAsGhostAsync(wallId, boulder.Id, ActivityType.BoulderCreated, name);
                }
                else
                {
                    await _activityLogService.LogAsync(wallId, boulder.Id, ActivityType.BoulderCreated, name);
                }

                // Notify at the SAME gate as the BoulderCreated activity: only when the boulder goes
                // live, never for a draft (a draft that is published later notifies in PublishBoulderAsync).
                // creatorId is the Ghost id for an anonymous-kiosk create, passed through as-is.
                if (_pushNotificationService is not null)
                {
                    await _pushNotificationService.NotifyBoulderAddedAsync(wallId, boulder.Id, creatorId);
                }
            }

            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder> PublishBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Publish");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders
                .Include(b => b.BoulderHolds)
                .Include(b => b.Setters)
                .FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Publish failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            // The creator, any of the boulder's setters, and any wall admin may publish the draft.
            var isCreator = boulder.CreatedByUserId == user.Id;
            var isSetter = boulder.Setters.Any(s => s.UserId == user.Id);
            if (!isCreator && !isSetter
                && !await WallAdminGuard.IsWallAdminAsync(db, boulder.WallId, user.Id, CancellationToken.None))
            {
                _logger.LogWarning(
                    "Publish denied: user {UserId} is neither creator {OwnerUserId}, a setter, nor an admin of boulder {BoulderId}",
                    user.Id, boulder.CreatedByUserId, boulderId);
                throw new InvalidOperationException("Only the creator, a setter, or a wall admin can publish a boulder");
            }

            if (!boulder.IsDraft)
            {
                return boulder;
            }

            if (boulder.BoulderHolds.Count == 0)
            {
                _logger.LogWarning("Publish rejected: boulder {BoulderId} has no holds", boulderId);
                throw new InvalidOperationException("Select at least one hold before publishing");
            }

            boulder.IsDraft = false;
            boulder.PublishedAt = DateTimeOffset.UtcNow;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == boulder.WallId);
            if (wall != null)
            {
                boulder.Generation = wall.CurrentGeneration;
            }

            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Boulder {BoulderId} published on wall {WallId} by {UserId}",
                boulderId, boulder.WallId, user.Id);

            await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.BoulderCreated, boulder.Name);

            // The draft has just gone live (the !IsDraft early return above means we only reach here
            // on the draft->live transition), so this is the single notify for a boulder that was
            // created as a draft and published later.
            if (_pushNotificationService is not null)
            {
                await _pushNotificationService.NotifyBoulderAddedAsync(boulder.WallId, boulderId, user.Id);
            }

            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CanCreateAnonymouslyAsync(Guid wallId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await KioskAnonymousSetting.IsAllowedAsync(db, _kioskContext, _kioskKeyValidator, wallId, ct);
    }

    /// <summary>
    /// The gate on the app's ONE unauthenticated write. Throws unless every condition in
    /// <see cref="KioskAnonymousSetting"/> holds AND the tablet is inside its volume budget.
    /// </summary>
    /// <remarks>
    /// Note what is NOT read here: <paramref name="wallId"/> is only ever COMPARED against the wall
    /// in the protected device cookie, never trusted from the caller. A form or route naming another
    /// wall fails the comparison and is refused, so there is no way to aim an anonymous write at a
    /// wall the tablet is not bolted to.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">This session may not create anonymously.</exception>
    private async Task EnsureAnonymousKioskCreateAllowedAsync(BlocwerkDbContext db, Guid wallId)
    {
        if (!await KioskAnonymousSetting.IsAllowedAsync(db, _kioskContext, _kioskKeyValidator, wallId))
        {
            // Deliberately the same exception an ordinary signed-out caller already got from
            // GetCurrentUserAsync, so nothing downstream has to learn a new failure mode and the
            // page above keeps sending them to sign in.
            throw new UnauthorizedAccessException(
                "Creating a boulder requires a signed-in user, or a kiosk tablet on a wall that allows it");
        }

        // The tablet is entitled; is it inside its budget? Missing throttle fails closed — an
        // unattended write surface with no cap is not something to default into.
        var apiKeyId = _kioskContext?.KioskApiKeyId ?? Guid.Empty;
        if (_kioskThrottle is null)
        {
            _logger.LogWarning(
                "Anonymous kiosk create refused on wall {WallId} with key {ApiKeyId}: no volume throttle is wired",
                wallId, apiKeyId);
            throw new UnauthorizedAccessException(
                "This tablet has created too many boulders recently. Sign in to keep setting.");
        }

        // CHECK only — the create is counted in RecordAnonymousKioskCreate once it has actually
        // landed, so a refused or failed write does not spend the tablet's or the wall's budget.
        // Checking here rather than only at the end still means a caller that is already at its cap
        // is turned away before any work is done.
        var budget = _kioskThrottle.Check(apiKeyId, wallId, DateTimeOffset.UtcNow);
        if (budget == KioskAnonymousSettingBudget.InstallationCapReached)
        {
            // Not routine throttling: the installation-wide backstop sits far above real load, so
            // reaching it is an incident. Logged at Error, and distinctly, so it is findable.
            _logger.LogError(
                "Anonymous kiosk create refused on wall {WallId} with key {ApiKeyId}: the INSTALLATION-WIDE "
                + "anonymous setting backstop of {MaxGlobal} per {Window} tripped — this is not ordinary load",
                wallId, apiKeyId, KioskAnonymousSettingThrottle.MaxGlobal, KioskAnonymousSettingThrottle.Window);
            throw new UnauthorizedAccessException(
                "Anonymous setting is temporarily unavailable. Sign in to keep setting.");
        }

        if (budget != KioskAnonymousSettingBudget.Allowed)
        {
            _logger.LogWarning(
                "Anonymous kiosk create refused on wall {WallId} with key {ApiKeyId}: {Budget}",
                wallId, apiKeyId, budget);
            throw new UnauthorizedAccessException(
                "This tablet has created too many boulders recently. Sign in to keep setting.");
        }
    }

    /// <summary>
    /// Counts one anonymous kiosk create that actually reached the database. Separate from the gate
    /// above on purpose: budget is spent by writes that happened, not by attempts.
    /// </summary>
    private void RecordAnonymousKioskCreate(Guid wallId)
    {
        _kioskThrottle?.Record(_kioskContext?.KioskApiKeyId ?? Guid.Empty, wallId, DateTimeOffset.UtcNow);
    }

    private async Task<Guid> ResolveViewerIdAsync()
    {
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            return user.Id;
        }
        catch (UnauthorizedAccessException) when (KioskViewing.ViewableWallId(_kioskContext) is not null)
        {
            return Guid.Empty;
        }
    }

    public async Task<Boulder?> GetBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Get");
        try
        {
            // Browsing boulders is the whole job of a wall-mounted tablet, and it does that with
            // nobody picked for most of the day. An anonymous kiosk therefore reads like a
            // share-token viewer — Guid.Empty opens the membership half of the wall filter — while
            // the reach-through to db.Walls below keeps it inside the kiosk's OWN wall, because
            // every context is stamped with that wall id. Any other anonymous caller still throws.
            var viewerId = await ResolveViewerIdAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = viewerId;

            return await db.Boulders

                // Attempts are unbounded and nobody reads boulder.Attempts off this call — the
                // detail page loads them separately (with the User) only when shown — so don't drag
                // them in here. Two collection includes remain (BoulderHolds + Setters), so split the
                // query to avoid a cartesian blow-up. AsNoTracking: read-only display.
                .AsSplitQuery()
                .AsNoTracking()
                .Include(b => b.BoulderHolds).ThenInclude(bh => bh.Hold)
                .Include(b => b.Setters).ThenInclude(s => s.User)
                .Include(b => b.CreatedBy)
                .Include(b => b.Wall)

                // Boulder carries no query filter of its own — the wall is the only filtered entity
                // in the model — so the id alone used to be the whole check and ANY signed-in caller
                // could read ANY boulder (name, grade, setters, holds) by guessing a guid. Reaching
                // through to db.Walls puts the read back under the membership filter, and picks up
                // the kiosk wall gate with it.
                .Where(b => db.Walls.Any(w => w.Id == b.WallId))
                .FirstOrDefaultAsync(b => b.Id == boulderId);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder?> GetBoulderByShareTokenAsync(Guid boulderId, string shareToken)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetByShareToken");
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            return await db.Boulders

                // See GetBoulderAsync: split the two collection includes to avoid a cartesian blow-up.
                .AsSplitQuery()
                .AsNoTracking()
                .Include(b => b.BoulderHolds).ThenInclude(bh => bh.Hold)
                .Include(b => b.Attempts.OrderByDescending(a => a.Timestamp)).ThenInclude(a => a.User)
                .Include(b => b.Setters).ThenInclude(s => s.User)
                .Include(b => b.CreatedBy)
                .Include(b => b.Wall)
                .Where(b => b.Id == boulderId && b.Wall.ShareToken == shareToken && !b.IsDraft)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<int?> GetHistoricGenerationAsync(
        Guid boulderId, string? shareToken = null, CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetHistoricGeneration");
        try
        {
            var isShare = !string.IsNullOrEmpty(shareToken);

            // The share path never resolves a viewer: Guid.Empty opens the membership half of the
            // wall filter and the share token below is the whole gate, mirroring
            // GetBoulderByShareTokenAsync. Only the normal path resolves a viewer (and still
            // tolerates an anonymous kiosk, which ResolveViewerIdAsync maps to Guid.Empty).
            var viewerId = isShare ? Guid.Empty : await ResolveViewerIdAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = viewerId;

            var gated = isShare
                ? db.Boulders.Where(b => b.Id == boulderId && b.Wall.ShareToken == shareToken)

                // Same reach-through as GetBoulderAsync: Boulder carries no filter of its own, so the
                // wall read is what puts this under membership (and the kiosk wall gate).
                : db.Boulders.Where(b => b.Id == boulderId && db.Walls.Any(w => w.Id == b.WallId));

            // One round trip: per boulder hold, its own generation and the generation of each ancestor
            // pointing at it (null when it has none), plus the wall's current generation to compare
            // against. Written as a LEFT JOIN rather than a per-row subquery because the latter needs
            // APPLY, which SQLite — what the tests run on — cannot translate.
            var rows = await (
                from b in gated
                from bh in b.BoulderHolds
                join link in db.HoldGenerationLinks on bh.HoldId equals link.NewHoldId into links
                from link in links.DefaultIfEmpty()
                select new
                {
                    b.Wall.CurrentGeneration,
                    OwnGeneration = bh.Hold.Generation,
                    AncestorGeneration = (int?)link.FromGeneration,
                })
                .ToListAsync(ct);

            if (rows.Count == 0)
            {
                return null;
            }

            // A hold carried into the current generation is a NEW row at that generation, so its own
            // generation says nothing about the change that flagged the boulder — its ancestor's does.
            // Prefer the ancestor's generation wherever there is one; the boulder is offered at the
            // MAX of those, i.e. the newest state that still predates today's wall.
            var resolved = rows.Max(r => r.AncestorGeneration ?? r.OwnGeneration);
            return resolved < rows[0].CurrentGeneration ? resolved : null;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoulderHoldAtGeneration>?> GetBoulderHoldsAtGenerationAsync(
        Guid boulderId, int generation, string? shareToken = null, CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetHoldsAtGeneration");
        try
        {
            var isShare = !string.IsNullOrEmpty(shareToken);

            // Same gate as GetHistoricGenerationAsync: the share path never resolves a viewer, the
            // token IS the gate; the normal path goes through membership (and the kiosk wall gate).
            var viewerId = isShare ? Guid.Empty : await ResolveViewerIdAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = viewerId;

            // The share arm carries the SAME !IsDraft filter as GetBoulderByShareTokenAsync. Without
            // it a share-token holder who guessed an unpublished boulder's id could read that draft's
            // hold geometry here, which no other read on the share path lets them do.
            var gated = isShare
                ? db.Boulders.Where(b => b.Id == boulderId && b.Wall.ShareToken == shareToken && !b.IsDraft)
                : db.Boulders.Where(b => b.Id == boulderId && db.Walls.Any(w => w.Id == b.WallId));

            var wallId = await gated.Select(b => (Guid?)b.WallId).FirstOrDefaultAsync(ct);
            if (wallId is not { } wall)
            {
                // Not readable at all — distinct from a boulder that simply had no holds back then.
                return null;
            }

            var marks = await LoadBoulderMarksAsync(gated, ct);
            if (marks.Count == 0)
            {
                return [];
            }

            var predecessor = await LoadPredecessorMapAsync(db, wall, generation, ct);
            var mapped = WalkBackToGeneration(marks, predecessor, generation);
            if (mapped.Count == 0)
            {
                return [];
            }

            return await LoadMappedHoldsAsync(db, wall, generation, mapped, ct);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<Boulder>> GetBouldersForWallAsync(Guid wallId, bool includeArchived = false)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetForWall", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var query = db.Boulders

                // Split the two collection includes (holds + attempts) to avoid a cartesian blow-up.
                .AsSplitQuery()
                .AsNoTracking()
                .Include(b => b.BoulderHolds)
                .Include(b => b.Attempts)
                .Include(b => b.CreatedBy)

                // Drafts are visible to every wall member (they just can't be logged until published),
                // so no creator-only draft filter here.
                .Where(b => b.WallId == wallId);

            if (!includeArchived)
            {
                query = query.Where(b => !b.IsArchived);
            }

            return await query.OrderByDescending(b => b.CreatedAt).ToListAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Dictionary<Guid, List<HoldUsageRef>>> GetHoldUsageAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetHoldUsage", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var rows = await db.BoulderHolds
                .AsNoTracking()
                .Where(bh => bh.Boulder.WallId == wallId && !bh.Boulder.IsArchived)

                // Drafts are visible to every wall member, so no creator-only draft filter here.
                .Select(bh => new
                {
                    bh.BoulderId,
                    bh.HoldId,
                    bh.Type,
                    bh.Usage,
                    bh.Boulder.Name,
                    bh.Boulder.Grade,
                    bh.Boulder.IsDraft,
                    bh.Boulder.IsHistoric,
                })
                .ToListAsync();

            // Load the wall's twin links once so membership can be expanded per boulder: a hold used
            // on one panel counts for every twin of that physical hold, and a boulder that saved both
            // twins is resolved to a single most-prominent entry (mirrors the detail viewer). A
            // single-panel wall has no links, so this collapses to the old per-hold-id grouping.
            var links = await db.HoldLinks
                .AsNoTracking()
                .Where(l => l.WallId == wallId)
                .Select(l => new HoldLinkPair(l.HoldAId, l.HoldBId))
                .ToListAsync();

            var usage = new Dictionary<Guid, List<HoldUsageRef>>();
            foreach (var boulder in rows.GroupBy(r => r.BoulderId))
            {
                var first = boulder.First();
                var reconciled = BoulderHoldReconciler.Reconcile(
                    boulder.Select(r => new ReconcilableHold(r.HoldId, r.Type, r.Usage)),
                    links);

                foreach (var physical in reconciled)
                {
                    var reference = new HoldUsageRef(
                        first.BoulderId,
                        first.Name,
                        first.Grade,
                        physical.Type,
                        physical.Usage,
                        first.IsDraft,
                        first.IsHistoric);

                    // Emit the boulder under every twin of the physical hold, so a one-sided boulder
                    // reports on the un-saved twin too and both twins show the same resolved Type/Usage.
                    foreach (var holdId in physical.HoldIds)
                    {
                        if (!usage.TryGetValue(holdId, out var refs))
                        {
                            refs = [];
                            usage[holdId] = refs;
                        }

                        refs.Add(reference);
                    }
                }
            }

            return usage.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.OrderBy(r => r.Name).ToList());
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder> UpdateBoulderAsync(
        Guid boulderId,
        string name,
        string? grade,
        List<BoulderHoldInput>? holds = null,
        bool? kickboardFootholdsOn = null,
        bool? handsFollowFeet = null,
        string? footColorOnly = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Update");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders
                .Include(b => b.BoulderHolds)
                .FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Update failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            boulder.Name = name;
            boulder.Grade = grade;

            if (kickboardFootholdsOn.HasValue)
            {
                boulder.KickboardFootholdsOn = kickboardFootholdsOn.Value;
            }

            if (handsFollowFeet.HasValue)
            {
                boulder.HandsFollowFeet = handsFollowFeet.Value;
            }

            if (footColorOnly != null)
            {
                boulder.FootColorOnly = NormalizeFootColor(footColorOnly);
            }

            if (holds != null)
            {
                db.BoulderHolds.RemoveRange(boulder.BoulderHolds);
                foreach (var h in EnforceHandsFollowFeet(holds, boulder.HandsFollowFeet))
                {
                    db.BoulderHolds.Add(new BoulderHold
                    {
                        BoulderId = boulderId,
                        HoldId = h.HoldId,
                        Type = h.Type,
                        Usage = h.Usage,
                    });
                }
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Boulder {BoulderId} updated by {UserId}", boulderId, user.Id);
            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder> RenameBoulderAsync(Guid boulderId, string name, string? grade)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Rename");
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _logger.LogWarning("Rename rejected: empty name for boulder {BoulderId}", boulderId);
                throw new InvalidOperationException("A boulder needs a name");
            }

            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders
                .Include(b => b.Setters)
                .FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Rename failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            // The creator, any of the boulder's setters, and any wall admin may edit its name/grade.
            var isCreator = boulder.CreatedByUserId == user.Id;
            var isSetter = boulder.Setters.Any(s => s.UserId == user.Id);
            if (!isCreator && !isSetter
                && !await WallAdminGuard.IsWallAdminAsync(db, boulder.WallId, user.Id, CancellationToken.None))
            {
                _logger.LogWarning(
                    "Rename denied: user {UserId} is neither creator {OwnerUserId}, a setter, nor an admin of boulder {BoulderId}",
                    user.Id, boulder.CreatedByUserId, boulderId);
                throw new InvalidOperationException("Only the creator, a setter, or a wall admin can edit this boulder");
            }

            boulder.Name = name.Trim();
            boulder.Grade = string.IsNullOrWhiteSpace(grade) ? null : grade.Trim();
            await db.SaveChangesAsync();
            _logger.LogInformation("Boulder {BoulderId} renamed by {UserId}", boulderId, user.Id);
            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder> ReviseBoulderAsync(
        Guid boulderId,
        List<BoulderHoldInput> updatedHolds,
        string? name = null,
        string? grade = null,
        bool? kickboardFootholdsOn = null,
        bool? handsFollowFeet = null,
        string? footColorOnly = null,
        bool noMatch = false,
        IReadOnlyList<Guid>? setterUserIds = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Revise");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders
                .Include(b => b.BoulderHolds)
                .Include(b => b.Setters)
                .FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Revise failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            // A boulder's creator may revise it, and so may any of its setters (co-setters) and any
            // wall admin — an admin may revise ANY historic boulder on their wall, not only the ones
            // they set themselves. NOTE: extending revise rights to wall moderators (WallRole.Moderator)
            // is a possible follow-up but deliberately not done here.
            var isCreator = boulder.CreatedByUserId == user.Id;
            var isSetter = boulder.Setters.Any(s => s.UserId == user.Id);
            var isAdmin = !isCreator && !isSetter
                && await WallAdminGuard.IsWallAdminAsync(db, boulder.WallId, user.Id, CancellationToken.None);
            if (!isCreator && !isSetter && !isAdmin)
            {
                _logger.LogWarning(
                    "Revise denied: user {UserId} is neither creator {OwnerUserId}, a setter, nor an admin of wall for boulder {BoulderId}",
                    user.Id, boulder.CreatedByUserId, boulderId);
                throw new InvalidOperationException(CreatorOrAdminRevisionMessage);
            }

            if (!boulder.IsHistoric && !boulder.IsDraft)
            {
                // A live/published boulder. An offline replay of an already-applied revise arrives
                // here after the first apply flipped IsHistoric off; if the requested state already
                // matches what is stored, this is that replay: return the boulder unchanged so the
                // queue records a success.
                if (RevisionIsNoOp(boulder, updatedHolds, name, grade, kickboardFootholdsOn, handsFollowFeet, footColorOnly, noMatch, setterUserIds))
                {
                    // ...except that reviewing a boulder and concluding nothing needs to change IS
                    // the point of the review: the setter opened it, checked it against the new
                    // photos and signed it off. Clearing the flag here keeps the replay idempotent
                    // anyway, because the first apply already left it false and a second pass then
                    // writes nothing.
                    if (boulder.NeedsReview)
                    {
                        boulder.NeedsReview = false;
                        await db.SaveChangesAsync();
                        _logger.LogInformation(
                            "Boulder {BoulderId} signed off as reviewed by {UserId} with no other change",
                            boulderId, user.Id);
                    }

                    return boulder;
                }

                // A live boulder that others have already sent used to be locked to name+grade edits
                // only. That block is now lifted for the creator, a setter, or a wall admin: they may
                // re-edit the holds in place even after others' sends (the existing ascents stay
                // attached to the boulder). The guard above already guarantees the reviser is one of
                // those, so this only fires for a non-creator/non-setter/non-admin — a case that is not
                // reachable here but kept explicit so HasBeenSentByOthersAsync and the const keep meaning.
                if (!isCreator && !isSetter && !isAdmin)
                {
                    var sentByOthers = await db.Attempts.AnyAsync(a =>
                        a.BoulderId == boulderId
                        && a.UserId != boulder.CreatedByUserId
                        && (a.Type == AttemptType.Send || a.Type == AttemptType.Flash));
                    if (sentByOthers)
                    {
                        _logger.LogWarning("Revise rejected: boulder {BoulderId} has already been sent by others", boulderId);
                        throw new InvalidOperationException(SentByOthersRevisionMessage);
                    }
                }
            }

            if (updatedHolds.Count == 0 && !boulder.IsDraft)
            {
                _logger.LogWarning("Revise rejected: boulder {BoulderId} has no holds", boulderId);
                throw new InvalidOperationException("Select at least one hold");
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                boulder.Name = name;
            }

            if (grade != null)
            {
                boulder.Grade = grade;
            }

            if (kickboardFootholdsOn.HasValue)
            {
                boulder.KickboardFootholdsOn = kickboardFootholdsOn.Value;
            }

            if (handsFollowFeet.HasValue)
            {
                boulder.HandsFollowFeet = handsFollowFeet.Value;
            }

            if (footColorOnly != null)
            {
                boulder.FootColorOnly = NormalizeFootColor(footColorOnly);
            }

            boulder.NoMatch = noMatch;

            db.BoulderHolds.RemoveRange(boulder.BoulderHolds);
            foreach (var h in EnforceHandsFollowFeet(updatedHolds, boulder.HandsFollowFeet))
            {
                db.BoulderHolds.Add(new BoulderHold
                {
                    BoulderId = boulderId,
                    HoldId = h.HoldId,
                    Type = h.Type,
                    Usage = h.Usage,
                });
            }

            // Replace the setter set when the caller supplied one (null = leave untouched).
            if (setterUserIds != null)
            {
                db.BoulderSetters.RemoveRange(boulder.Setters);
                await AddSettersAsync(db, boulderId, boulder.WallId, setterUserIds);
            }

            // Remapping onto the current hold model resolves both staleness flags.
            boulder.IsHistoric = false;
            boulder.NeedsReview = false;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == boulder.WallId);
            if (wall != null)
            {
                boulder.Generation = wall.CurrentGeneration;
            }

            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Boulder {BoulderId} revised on wall {WallId} by {UserId}",
                boulderId, boulder.WallId, user.Id);

            await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.BoulderRevised);
            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task DeleteBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Delete");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders
                .Include(b => b.Setters)
                .FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Delete failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            // Same authority as archive and grade-proposal accept/reject: creator, any setter, or a
            // wall admin. Deleting is the one irreversible member of that family, so it may not be
            // the one action every signed-in account in the installation can reach by guessing an id.
            if (!await CanManageBoulderAsync(db, boulder, user.Id))
            {
                _logger.LogWarning(
                    "Delete rejected: user {UserId} may not delete boulder {BoulderId} on wall {WallId}",
                    user.Id, boulderId, boulder.WallId);
                throw new InvalidOperationException("Only the creator, a setter, or a wall admin can delete a boulder");
            }

            db.Boulders.Remove(boulder);
            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordBoulderDeleted(boulder.WallId);
            _logger.LogInformation(
                "Boulder {BoulderId} deleted from wall {WallId} by {UserId}",
                boulderId, boulder.WallId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task ArchiveBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Archive");
        try
        {
            await SetArchivedAsync(boulderId, true);
            _logger.LogInformation("Boulder {BoulderId} archived", boulderId);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task UnarchiveBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Unarchive");
        try
        {
            await SetArchivedAsync(boulderId, false);
            _logger.LogInformation("Boulder {BoulderId} unarchived", boulderId);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<GradeProposal> ProposeGradeAsync(Guid boulderId, string proposedGrade)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.ProposeGrade");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Propose grade failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            if (boulder.CreatedByUserId == user.Id)
            {
                _logger.LogWarning(
                    "Propose grade denied: user {UserId} is the creator of boulder {BoulderId}",
                    user.Id, boulderId);
                throw new InvalidOperationException("Cannot propose a grade for your own boulder");
            }

            var existing = await db.GradeProposals
                .FirstOrDefaultAsync(gp => gp.BoulderId == boulderId && !gp.IsResolved);

            if (existing != null)
            {
                existing.ProposedGrade = proposedGrade;
                existing.ProposedByUserId = user.Id;
                existing.CreatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                _logger.LogInformation(
                    "Grade proposal {ProposalId} updated for boulder {BoulderId} by {UserId}",
                    existing.Id, boulderId, user.Id);
                existing.ProposedBy = user;
                return existing;
            }

            var proposal = new GradeProposal
            {
                BoulderId = boulderId,
                ProposedByUserId = user.Id,
                ProposedGrade = proposedGrade,
            };

            db.GradeProposals.Add(proposal);
            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Grade proposal {ProposalId} created for boulder {BoulderId} by {UserId}",
                proposal.Id, boulderId, user.Id);

            proposal.ProposedBy = user;
            return proposal;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<GradeProposal?> GetActiveProposalAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetActiveProposal");
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            return await db.GradeProposals
                .Include(gp => gp.ProposedBy)
                .FirstOrDefaultAsync(gp => gp.BoulderId == boulderId && !gp.IsResolved);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task AcceptGradeProposalAsync(Guid proposalId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.AcceptGradeProposal");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var proposal = await db.GradeProposals
                .Include(gp => gp.Boulder)
                .ThenInclude(b => b.Setters)
                .FirstOrDefaultAsync(gp => gp.Id == proposalId && !gp.IsResolved);
            if (proposal == null)
            {
                _logger.LogWarning("Accept grade failed: proposal {ProposalId} not found for user {UserId}", proposalId, user.Id);
                throw new InvalidOperationException("Proposal not found");
            }

            if (!await CanManageBoulderAsync(db, proposal.Boulder, user.Id))
            {
                _logger.LogWarning(
                    "Accept grade denied: user {UserId} is neither creator {OwnerUserId}, a setter, nor an admin of boulder {BoulderId} (proposal {ProposalId})",
                    user.Id, proposal.Boulder.CreatedByUserId, proposal.BoulderId, proposalId);
                throw new InvalidOperationException("Only the creator, a setter, or a wall admin can accept a grade proposal");
            }

            proposal.Boulder.Grade = proposal.ProposedGrade;
            proposal.IsResolved = true;
            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Grade proposal {ProposalId} accepted for boulder {BoulderId} by {UserId}",
                proposalId, proposal.BoulderId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task RejectGradeProposalAsync(Guid proposalId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.RejectGradeProposal");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var proposal = await db.GradeProposals
                .Include(gp => gp.Boulder)
                .ThenInclude(b => b.Setters)
                .FirstOrDefaultAsync(gp => gp.Id == proposalId && !gp.IsResolved);
            if (proposal == null)
            {
                _logger.LogWarning("Reject grade failed: proposal {ProposalId} not found for user {UserId}", proposalId, user.Id);
                throw new InvalidOperationException("Proposal not found");
            }

            if (!await CanManageBoulderAsync(db, proposal.Boulder, user.Id))
            {
                _logger.LogWarning(
                    "Reject grade denied: user {UserId} is neither creator {OwnerUserId}, a setter, nor an admin of boulder {BoulderId} (proposal {ProposalId})",
                    user.Id, proposal.Boulder.CreatedByUserId, proposal.BoulderId, proposalId);
                throw new InvalidOperationException("Only the creator, a setter, or a wall admin can reject a grade proposal");
            }

            proposal.IsResolved = true;
            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Grade proposal {ProposalId} rejected for boulder {BoulderId} by {UserId}",
                proposalId, proposal.BoulderId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// While hands follow feet the boulder has no dedicated footholds, so a
    /// <see cref="HoldUsage.FootOnly"/> mark is contradictory. It is coerced rather than
    /// rejected, so no client can push the boulder into an inconsistent state.
    /// </summary>
    private static List<BoulderHoldInput> EnforceHandsFollowFeet(List<BoulderHoldInput> holds, bool handsFollowFeet)
    {
        if (!handsFollowFeet)
        {
            return holds;
        }

        return holds
            .Select(h => h.Usage == HoldUsage.FootOnly ? h with { Usage = HoldUsage.HandAndFoot } : h)
            .ToList();
    }

    /// <summary>
    /// Inserts a <see cref="BoulderSetter"/> row for each supplied user id that is actually a member
    /// of the wall — non-members (and duplicates) are silently skipped, so an unknown or off-wall id
    /// can never attach a phantom setter. Adds to the change tracker only; the caller saves.
    /// </summary>
    private static async Task AddSettersAsync(
        BlocwerkDbContext db,
        Guid boulderId,
        Guid wallId,
        IReadOnlyList<Guid>? setterUserIds)
    {
        if (setterUserIds is not { Count: > 0 })
        {
            return;
        }

        var distinct = setterUserIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return;
        }

        var memberIds = await db.WallMembers
            .Where(m => m.WallId == wallId && distinct.Contains(m.UserId))
            .Select(m => m.UserId)
            .ToListAsync();

        foreach (var userId in memberIds)
        {
            db.BoulderSetters.Add(new BoulderSetter { BoulderId = boulderId, UserId = userId });
        }
    }

    /// <summary>
    /// An empty foot color means "no foot color rule"; anything else is stored as given.
    /// </summary>
    private static string? NormalizeFootColor(string? footColorOnly) =>
        string.IsNullOrWhiteSpace(footColorOnly) ? null : footColorOnly;

    /// <summary>
    /// True when applying this revise would leave the boulder exactly as it already is, so the
    /// call is a no-op replay of an earlier apply. Only fields the caller actually supplied are
    /// compared, mirroring the "null argument leaves the stored value untouched" contract.
    /// </summary>
    private static bool RevisionIsNoOp(
        Boulder boulder,
        List<BoulderHoldInput> updatedHolds,
        string? name,
        string? grade,
        bool? kickboardFootholdsOn,
        bool? handsFollowFeet,
        string? footColorOnly,
        bool noMatch,
        IReadOnlyList<Guid>? setterUserIds)
    {
        var effectiveHandsFollowFeet = handsFollowFeet ?? boulder.HandsFollowFeet;
        var target = EnforceHandsFollowFeet(updatedHolds, effectiveHandsFollowFeet)
            .Select(h => (h.HoldId, h.Type, h.Usage))
            .ToHashSet();
        var current = boulder.BoulderHolds
            .Select(h => (h.HoldId, h.Type, h.Usage))
            .ToHashSet();

        if (!current.SetEquals(target))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(name) && boulder.Name != name)
        {
            return false;
        }

        if (grade != null && boulder.Grade != grade)
        {
            return false;
        }

        if (kickboardFootholdsOn.HasValue && boulder.KickboardFootholdsOn != kickboardFootholdsOn.Value)
        {
            return false;
        }

        if (handsFollowFeet.HasValue && boulder.HandsFollowFeet != handsFollowFeet.Value)
        {
            return false;
        }

        if (footColorOnly != null && boulder.FootColorOnly != NormalizeFootColor(footColorOnly))
        {
            return false;
        }

        // noMatch is a plain bool the revise path always writes (see ReviseBoulderAsync), so a
        // revision that only flips it must not be mistaken for a no-op replay.
        if (boulder.NoMatch != noMatch)
        {
            return false;
        }

        // Setters are replaced only when the caller supplied them (null = leave untouched). A
        // supplied set that differs from the stored setters is a real change, not a replay.
        if (setterUserIds != null)
        {
            var targetSetters = setterUserIds.Where(id => id != Guid.Empty).ToHashSet();
            var currentSetters = boulder.Setters.Select(s => s.UserId).ToHashSet();
            if (!currentSetters.SetEquals(targetSetters))
            {
                return false;
            }
        }

        return true;
    }

    private async Task SetArchivedAsync(Guid boulderId, bool archived)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders
                          .Include(b => b.Setters)
                          .FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        if (!await CanManageBoulderAsync(db, boulder, user.Id))
        {
            throw new InvalidOperationException("Only the creator, a setter, or a wall admin can archive a boulder");
        }

        if (archived && !boulder.IsHistoric)
        {
            throw new InvalidOperationException("Only historic boulders can be archived");
        }

        boulder.IsArchived = archived;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The ONE authority rule for acting on somebody else's boulder: its creator, any of its
    /// setters, or an admin of its wall. Publish, rename, revise, grade-proposal accept/reject,
    /// archive and delete all use it, so a boulder can never end up with an action only one of those roles can
    /// reach.
    /// </summary>
    /// <remarks>
    /// A creator-only rule wedges permanently on a Ghost boulder: nobody signs in as the Ghost row,
    /// so the creator branch is false for every human being. That left an open grade proposal that
    /// no setter, wall admin or app admin could resolve — and, before this, an unattended-kiosk
    /// boulder that could never be archived when the gym reset the wall.
    /// <para>
    /// <paramref name="boulder"/> must have its <see cref="Boulder.Setters"/> loaded; the wall-admin
    /// branch runs against the DB and carries the kiosk foreign-wall check with it.
    /// </para>
    /// </remarks>
    private static async Task<bool> CanManageBoulderAsync(BlocwerkDbContext db, Boulder boulder, Guid userId)
    {
        if (boulder.CreatedByUserId == userId)
        {
            return true;
        }

        if (boulder.Setters.Any(s => s.UserId == userId))
        {
            return true;
        }

        return await WallAdminGuard.IsWallAdminAsync(db, boulder.WallId, userId, CancellationToken.None);
    }

    /// <summary>
    /// The boulder's own marks, one per marked hold, each carrying the generation that hold row
    /// itself lives at — the starting point of the backward walk.
    /// </summary>
    private static async Task<List<(Guid HoldId, HoldType Type, HoldUsage Usage, int OwnGeneration)>>
        LoadBoulderMarksAsync(IQueryable<Boulder> gated, CancellationToken ct)
    {
        var rows = await (
            from b in gated
            from bh in b.BoulderHolds
            select new
            {
                bh.HoldId,
                bh.Type,
                bh.Usage,
                OwnGeneration = bh.Hold.Generation,
            })
            .ToListAsync(ct);

        return rows.Select(r => (r.HoldId, r.Type, r.Usage, r.OwnGeneration)).ToList();
    }

    /// <summary>
    /// Reads the hold rows the walk resolved to and pairs each with the boulder's mark for it. The
    /// generation filter is kept on the read so a mapped id that is somehow not at that generation
    /// drops out rather than drawing a row from the wrong one.
    /// </summary>
    private static async Task<List<BoulderHoldAtGeneration>> LoadMappedHoldsAsync(
        BlocwerkDbContext db,
        Guid wallId,
        int generation,
        Dictionary<Guid, MappedMark> mapped,
        CancellationToken ct)
    {
        var ids = mapped.Keys.ToList();
        var holds = await db.Holds
            .AsNoTracking()
            .Where(hold => hold.WallId == wallId && ids.Contains(hold.Id) && hold.Generation == generation)
            .ToListAsync(ct);

        return holds
            .Select(hold => new BoulderHoldAtGeneration(hold, mapped[hold.Id].Type, mapped[hold.Id].Usage)
            {
                // Every one of the boulder's holds that landed on this row, so the caller can tell a
                // hold that genuinely failed to translate from several that converged here.
                SourceHoldIds = mapped[hold.Id].SourceHoldIds,
            })
            .ToList();
    }

    /// <summary>
    /// The wall's hold lineage from <paramref name="generation"/> forward, reduced to ONE backward
    /// step per hold: successor id -&gt; (predecessor id, the generation that predecessor lived at).
    /// </summary>
    /// <remarks>
    /// One round trip, walked in memory: neither provider we run on translates a recursive CTE here
    /// portably, and a wall's link count is in the low thousands. FromGeneration is read off the LINK
    /// rather than off the predecessor hold so the chain still walks through a tombstoned predecessor
    /// (OldHoldId nulled by HoldDeletion).
    /// <para>
    /// A single row can have SEVERAL ancestors — two older holds recorded as becoming one — and only
    /// one backward step can be taken, so the pick is made EXPLICITLY here instead of being left to
    /// the database's result order: that order is unspecified for this query, and letting it decide
    /// meant the historic view could move a boulder onto a different hold between two identical
    /// reads. Tie-break, in order: the latest recorded link first (the closest predecessor in time,
    /// so the walk never jumps over a generation that also has a row for this hold), then the
    /// smallest predecessor id — arbitrary, but identical on every read, machine and provider.
    /// </para>
    /// </remarks>
    private static async Task<Dictionary<Guid, (Guid OldId, int FromGeneration)>> LoadPredecessorMapAsync(
        BlocwerkDbContext db,
        Guid wallId,
        int generation,
        CancellationToken ct)
    {
        var steps = await db.HoldGenerationLinks
            .AsNoTracking()
            .Where(l => l.WallId == wallId
                && l.NewHoldId != null
                && l.OldHoldId != null
                && l.FromGeneration >= generation)
            .Select(l => new { NewId = l.NewHoldId!.Value, OldId = l.OldHoldId!.Value, l.FromGeneration })
            .ToListAsync(ct);

        return steps
            .GroupBy(s => s.NewId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var pick = g.OrderByDescending(s => s.FromGeneration).ThenBy(s => s.OldId).First();
                    return (pick.OldId, pick.FromGeneration);
                });
    }

    /// <summary>
    /// Walks each of the boulder's marked holds back along <paramref name="predecessor"/> until it
    /// sits at <paramref name="generation"/>, and returns the boulder's marks keyed by the hold id AT
    /// that generation. A hold already there maps to itself; one whose chain runs out before the
    /// target did not exist then and drops out.
    /// </summary>
    /// <remarks>
    /// Several of the boulder's holds can converge on ONE row at the target generation (that row was
    /// later recorded as becoming each of them), so the convergence has to be resolved down to a
    /// single mark. <paramref name="marks"/> is SORTED first rather than resolved in arrival order:
    /// the caller's rows come back in whatever order the provider chose, and first-wins over an
    /// unordered list is not reproducible. The order is the most prominent mark first (Top &gt; Start
    /// &gt; Normal, so the old row is drawn as the start or top it became), then the lower
    /// <see cref="HoldUsage"/>, then the smallest hold id — the last two are arbitrary, but stable.
    /// </remarks>
    private static Dictionary<Guid, MappedMark> WalkBackToGeneration(
        IReadOnlyList<(Guid HoldId, HoldType Type, HoldUsage Usage, int OwnGeneration)> marks,
        IReadOnlyDictionary<Guid, (Guid OldId, int FromGeneration)> predecessor,
        int generation)
    {
        var mapped = new Dictionary<Guid, MappedMark>();
        var sources = new Dictionary<Guid, List<Guid>>();
        var ordered = marks
            .OrderByDescending(m => m.Type)
            .ThenBy(m => m.Usage)
            .ThenBy(m => m.HoldId);

        foreach (var mark in ordered)
        {
            var cursor = mark.HoldId;
            var cursorGeneration = mark.OwnGeneration;
            while (cursorGeneration > generation
                && predecessor.TryGetValue(cursor, out var step)

                // Each hop must move strictly back in time: a malformed link (or a cycle) would
                // otherwise spin here forever on a page load.
                && step.FromGeneration < cursorGeneration)
            {
                cursor = step.OldId;
                cursorGeneration = step.FromGeneration;
            }

            if (cursorGeneration != generation)
            {
                continue;
            }

            // The marks are pre-sorted, so the first one to reach a row is already the winner. A
            // later mark reaching the same row is a CONVERGENCE, not a failure: it is recorded as a
            // second source so the caller does not count it as a hold with no match.
            if (!sources.TryGetValue(cursor, out var reached))
            {
                reached = [];
                sources[cursor] = reached;
                mapped[cursor] = new MappedMark(mark.Type, mark.Usage, reached);
            }

            reached.Add(mark.HoldId);
        }

        return mapped;
    }
}
