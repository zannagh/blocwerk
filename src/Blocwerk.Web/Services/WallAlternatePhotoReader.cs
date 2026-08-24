using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Services;

/// <summary>One stored wall image plus the content type it was uploaded with.</summary>
public sealed record WallAlternatePhoto(byte[] Content, string ContentType);

/// <summary>
/// Reads the second projection of a wall photo (<c>PhotoAlternate</c> / <c>StagedPhotoAlternate</c>)
/// for the photo endpoints.
/// </summary>
/// <remarks>
/// Access control is the database's: <c>BlocwerkDbContext</c> filters walls by
/// <c>CurrentUserId</c>, so setting it to the signed-in user (or to <see cref="Guid.Empty"/> plus
/// an explicit share-token predicate) gives exactly the same visibility as
/// <c>WallService.GetPhotoAsync</c>/<c>GetPhotoByShareTokenAsync</c>. This lives in the web layer
/// because <c>IWallService</c> has no alternate-projection accessor.
/// </remarks>
public sealed class WallAlternatePhotoReader
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;

    public WallAlternatePhotoReader(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
    }

    /// <summary>The live alternate projection, for a signed-in member of the wall.</summary>
    public async Task<WallAlternatePhoto?> GetAlternateAsync(Guid wallId, CancellationToken ct = default)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;

        var row = await db.Walls
            .AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new { w.PhotoAlternate, w.PhotoAlternateContentType })
            .FirstOrDefaultAsync(ct);

        return Materialise(row?.PhotoAlternate, row?.PhotoAlternateContentType);
    }

    /// <summary>The live alternate projection for an anonymous share-link viewer.</summary>
    public async Task<WallAlternatePhoto?> GetAlternateByShareTokenAsync(
        Guid wallId,
        string shareToken,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var row = await db.Walls
            .AsNoTracking()
            .Where(w => w.Id == wallId && w.ShareToken == shareToken)
            .Select(w => new { w.PhotoAlternate, w.PhotoAlternateContentType })
            .FirstOrDefaultAsync(ct);

        return Materialise(row?.PhotoAlternate, row?.PhotoAlternateContentType);
    }

    /// <summary>The staged alternate projection of an update awaiting confirmation.</summary>
    public async Task<WallAlternatePhoto?> GetStagedAlternateAsync(Guid wallId, CancellationToken ct = default)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;

        var row = await db.Walls
            .AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new { w.StagedPhotoAlternate, w.StagedPhotoAlternateContentType })
            .FirstOrDefaultAsync(ct);

        return Materialise(row?.StagedPhotoAlternate, row?.StagedPhotoAlternateContentType);
    }

    private static WallAlternatePhoto? Materialise(byte[]? content, string? contentType)
    {
        if (content is null || content.Length == 0)
        {
            return null;
        }

        return new WallAlternatePhoto(content, string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType);
    }
}
