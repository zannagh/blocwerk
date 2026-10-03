using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IWallPhotoPrivacyService"/>. Every stored photo of the wall is one <see cref="WallPhotoSlot"/>
/// (see the Slots partial); the run loads, inspects and — when applying — rewrites them one at a time on
/// a fresh context, so at most one photo is held in memory and a long run never pins a big change tracker.
/// </summary>
/// <remarks>
/// The rewrites go through <c>ExecuteUpdate</c> and so bypass the change journal and the domain-change
/// broadcast on purpose: removing metadata is not a wall edit to replay or revert, and nothing cached per
/// circuit holds photo bytes. The journal's own copies of the photos are cleaned in the same run instead.
/// Browsers refetch without any extra version bump: every image ETag and variant-cache key includes the
/// stored length, which shrinks whenever something is removed.
/// </remarks>
public sealed partial class WallPhotoPrivacyService : IWallPhotoPrivacyService
{
    private const string AdminAction = "Removing location data from wall photos";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IWallImageStorage imageStorage;
    private readonly ILogger<WallPhotoPrivacyService> logger;
    private readonly IKioskContext? kioskContext;

    public WallPhotoPrivacyService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IWallImageStorage imageStorage,
        ILogger<WallPhotoPrivacyService> logger,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.imageStorage = imageStorage;
        this.logger = logger;
        this.kioskContext = kioskContext;
    }

    /// <inheritdoc/>
    public Task<PhotoPrivacyReport> ScanAsync(Guid wallId, CancellationToken ct = default) =>
        RunAsync(wallId, apply: false, ct);

    /// <inheritdoc/>
    public Task<PhotoPrivacyReport> RemoveMetadataAsync(Guid wallId, CancellationToken ct = default) =>
        RunAsync(wallId, apply: true, ct);

    private async Task<PhotoPrivacyReport> RunAsync(Guid wallId, bool apply, CancellationToken ct)
    {
        List<WallPhotoSlot> slots;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            var user = await currentUserService.GetCurrentUserAsync();
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, AdminAction);
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
            slots = await CollectSlotsAsync(db, wallId, ct);
        }

        int photos = 0, withLocation = 0, withMetadata = 0, cleaned = 0, skipped = 0;
        foreach (var slot in slots)
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = Guid.Empty;
            var original = await slot.Load(db, ct);
            if (original is not { Length: > 0 })
            {
                continue;
            }

            photos++;
            var facts = StoredPhotoSanitizer.Inspect(original);
            withLocation += facts.HasLocation ? 1 : 0;
            if (!facts.NeedsCleaning)
            {
                continue;
            }

            withMetadata++;
            if (apply)
            {
                var written = await slot.Save(db, original, StoredPhotoSanitizer.Sanitize(original), ct);
                cleaned += written ? 1 : 0;
                skipped += written ? 0 : 1;
            }
        }

        var report = new PhotoPrivacyReport(photos, withLocation, withMetadata, cleaned, skipped);
        logger.LogInformation(
            "Photo metadata {Mode} on wall {WallId}: {Photos} photos, {WithLocation} with location, {WithMetadata} with metadata, {Cleaned} cleaned, {Skipped} skipped",
            apply ? "removal" : "scan", wallId, photos, withLocation, withMetadata, cleaned, skipped);
        return report;
    }
}
