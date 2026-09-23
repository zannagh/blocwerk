using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldOutlineUpgradeService"/>: walks a wall's live photos (every live panel, plus a legacy
/// single-image wall's photo), decodes each ONCE and outlines its circle holds with <see cref="IHoldOutlineService"/>.
/// Gate: <see cref="WallAdminGuard"/> (owner or admin) plus <see cref="KioskGuard"/> — an owner-desk task,
/// never a tablet one; a share-link visitor has no signed-in user and is refused by the user lookup.
/// </summary>
/// <remarks>The outliner is optional: a host without the HoldDetection project reports the action as off.</remarks>
public sealed partial class HoldOutlineUpgradeService : IHoldOutlineUpgradeService
{
    /// <summary>Holds written per SaveChanges (each batch also updates the run record, atomically).</summary>
    public const int BatchSize = 100;

    private const int ExampleCount = 5;
    private const string KioskRefusal = "Upgrading hold outlines";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly HoldDetectionSettings settings;
    private readonly ILogger<HoldOutlineUpgradeService> logger;
    private readonly IHoldOutlineService? outlineService;
    private readonly IKioskContext? kioskContext;

    /// <summary>Initializes a new instance of the <see cref="HoldOutlineUpgradeService"/> class.</summary>
    /// <param name="dbContextFactory">Context factory.</param>
    /// <param name="currentUserService">The acting user.</param>
    /// <param name="settings">App settings (the outline / marker kill switches).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="outlineService">The outliner; null means the action is unavailable.</param>
    /// <param name="kioskContext">The kiosk context, when the host has one.</param>
    public HoldOutlineUpgradeService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        BlocwerkSettings settings,
        ILogger<HoldOutlineUpgradeService> logger,
        IHoldOutlineService? outlineService = null,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.settings = settings.HoldDetection;
        this.logger = logger;
        this.outlineService = outlineService;
        this.kioskContext = kioskContext;
    }

    private bool Enabled => settings.OutlinesEnabled && outlineService is not null;

    /// <inheritdoc/>
    public async Task<HoldOutlineUpgradeStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            // Few rows per wall; ordered in memory because SQLite cannot ORDER BY a DateTimeOffset.
            var runs = await db.HoldOutlineUpgradeRuns.AsNoTracking().Where(r => r.WallId == wallId).ToListAsync(ct);
            var latest = runs.MaxBy(r => r.CreatedAt);
            var info = latest is null
                ? null
                : new HoldOutlineUpgradeRunInfo(latest.Id, latest.CreatedAt, latest.OutlinedCount, latest.FingerprintedCount, latest.RevertedAt);
            return new HoldOutlineUpgradeStatus(Enabled, info);
        }
    }

    /// <inheritdoc/>
    public async Task<HoldOutlineUpgradePreview> PreviewAsync(
        Guid wallId, HoldOutlineUpgradeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureEnabled();
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var photos = await LoadLivePhotosAsync(db, wallId, ct);
            var proposals = new List<HoldOutlineUpgradeProposal>();
            foreach (var photo in photos)
            {
                var plan = await PlanPhotoAsync(db, wallId, photo, options.IncludeManual, metricModel: null, ct);
                proposals.AddRange(plan?.Proposals ?? []);
            }

            return Summarize(photos.Count, proposals);
        }
    }

    /// <summary>Counts a dry run's proposals.</summary>
    /// <param name="photos">Photos looked at.</param>
    /// <param name="proposals">All proposals.</param>
    /// <returns>The preview.</returns>
    public static HoldOutlineUpgradePreview Summarize(int photos, IReadOnlyCollection<HoldOutlineUpgradeProposal> proposals)
    {
        var outlined = proposals.Where(p => p.Outcome == HoldOutlineUpgradeOutcome.Outline).ToList();
        return new HoldOutlineUpgradePreview
        {
            Photos = photos,
            Eligible = proposals.Count,
            ByContour = outlined.Count(p => p.Result.Method == HoldOutlineMethod.Contour),
            ByGrabCut = outlined.Count(p => p.Result.Method == HoldOutlineMethod.GrabCut),
            WouldKeepCircle = proposals.Count - outlined.Count,
            RejectedAsLeak = proposals.Count(p => p.Outcome == HoldOutlineUpgradeOutcome.RejectedLeak),
            WithHoles = outlined.Count(p => p.HasHoles),
            WouldFingerprint = proposals.Count(p => p.FillsFingerprint),
            ExampleHoldIds = outlined.Take(ExampleCount).Select(p => p.Hold.Id).ToList(),
        };
    }

    private void EnsureEnabled()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Outline detection is switched off on this server.");
        }
    }

    /// <summary>Opens a context for a wall admin, refused outright from any kiosk tablet.</summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, KioskRefusal);
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
            return (db, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
