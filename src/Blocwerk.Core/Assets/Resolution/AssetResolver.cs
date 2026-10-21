// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// <see cref="IAssetResolver"/> over the hash columns of PR 2.1. Every lookup is scoped by the wall (geometry through
/// <c>GeometryModel.WallId</c>, videos through their boulder), so a sha of wall A asked for under wall B is a 404 even
/// for a member of both. Rows whose hash is still null are simply not found: their bytes stay on the legacy routes.
/// </summary>
public sealed class AssetResolver(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IWallImageStorage imageStorage,
    ICaptureFileStore captureFiles,
    FileHashMemo fileHashes,
    ILogger<AssetResolver> logger,
    IKioskContext? kioskContext = null) : IAssetResolver
{
    /// <inheritdoc/>
    public async Task<ResolvedAsset?> ResolveAsync(Guid wallId, string sha, string? shareToken, CancellationToken ct)
    {
        var hash = ParseSha(sha);
        if (hash is null)
        {
            return null;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var access = await AssetAccessGate.DecideAsync(db, wallId, shareToken, currentUserService, kioskContext, ct);
        if (access == AssetAccess.None)
        {
            return null;
        }

        return await AssetBlobLookups.PanelAsync(db, wallId, hash, ct)
            ?? await AssetBlobLookups.WallPhotoAsync(db, wallId, hash, ct)
            ?? await AssetBlobLookups.ResetPhotoAsync(db, wallId, hash, ct)
            ?? await AssetFileLookups.GalleryAsync(db, imageStorage, wallId, hash, ct)
            ?? await AssetFileLookups.TextureAsync(db, captureFiles, wallId, hash, ct)
            ?? await AssetFileLookups.MaskAsync(db, captureFiles, wallId, hash, ct)
            ?? await AssetFileLookups.SplatAsync(db, captureFiles, wallId, hash, ct)
            ?? await AssetBlobLookups.VideoThumbnailAsync(db, wallId, hash, access, ct);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> LoadAsync(ResolvedAsset asset, CancellationToken ct)
    {
        if (asset.IsStreamed)
        {
            return null;
        }

        byte[]? bytes;
        if (asset.PhysicalPath is { } path)
        {
            bytes = await ReadFileAsync(path, ct);
        }
        else
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            bytes = await AssetBlobLookups.LoadAsync(db, asset, ct);
        }

        if (bytes is null || ContentHash.Same(ContentHash.Of(bytes), asset.Sha256))
        {
            return bytes;
        }

        logger.LogWarning("{Kind} {RowId} no longer hashes to {Sha}; refusing to serve it under that hash", asset.Kind, asset.RowId, asset.Hex);
        return null;
    }

    /// <inheritdoc/>
    public async Task<bool> VerifyFileAsync(ResolvedAsset asset, CancellationToken ct)
    {
        if (asset.PhysicalPath is not { } path)
        {
            return false;
        }

        try
        {
            if (await fileHashes.MatchesAsync(path, asset.Sha256, ct))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not hash {Kind} {RowId} at {Path}", asset.Kind, asset.RowId, path);
            return false;
        }

        logger.LogWarning("{Kind} {RowId} no longer hashes to {Sha}; refusing to serve it under that hash", asset.Kind, asset.RowId, asset.Hex);
        return false;
    }

    /// <summary>
    /// The 32 bytes of a route sha, which must be exactly 64 LOWERCASE hex characters: the form every hashed URL is
    /// built in, and the only one the service worker's route pattern matches, so one asset has one URL.
    /// </summary>
    internal static byte[]? ParseSha(string? sha)
    {
        var hash = ContentHash.FromHex(sha);
        return hash is not null && string.Equals(ContentHash.ToHex(hash), sha, StringComparison.Ordinal) ? hash : null;
    }

    private async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read asset file {Path}", path);
            return null;
        }
    }
}
