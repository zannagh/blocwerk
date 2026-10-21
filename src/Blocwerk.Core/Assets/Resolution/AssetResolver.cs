// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Manifest;
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
    VerifiedBlobMemo verifiedBlobs,
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

        var asset = await LookUpAsync(db, wallId, hash, access, ct);
        return asset is null || kioskContext is not { IsKiosk: true } || ManifestAudiences.Lists(ManifestAudience.Kiosk, asset.Kind)
            ? asset
            : asset with { NoStore = true };
    }

    /// <inheritdoc/>
    public async Task<byte[]?> LoadAsync(ResolvedAsset asset, CancellationToken ct)
    {
        if (asset.IsStreamed)
        {
            return null;
        }

        if (asset.PhysicalPath is { } path)
        {
            return await ReadFileAsync(asset, path, ct);
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var bytes = await AssetBlobLookups.LoadAsync(db, asset, ct);
        if (bytes is null || verifiedBlobs.Verify(asset, bytes))
        {
            return bytes;
        }

        LogMismatch(asset);
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

        LogMismatch(asset);
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

    private async Task<ResolvedAsset?> LookUpAsync(BlocwerkDbContext db, Guid wallId, byte[] hash, AssetAccess access, CancellationToken ct)
    {
        return await AssetBlobLookups.PanelAsync(db, wallId, hash, ct)
            ?? await AssetBlobLookups.WallPhotoAsync(db, wallId, hash, ct)
            ?? await AssetBlobLookups.ResetPhotoAsync(db, wallId, hash, ct)
            ?? await AssetFileLookups.GalleryAsync(db, imageStorage, wallId, hash, ct)
            ?? await AssetFileLookups.TextureAsync(db, captureFiles, wallId, hash, ct)
            ?? await AssetFileLookups.MaskAsync(db, captureFiles, wallId, hash, ct)
            ?? await AssetFileLookups.SplatAsync(db, captureFiles, wallId, hash, ct)
            ?? await AssetBlobLookups.VideoThumbnailAsync(db, wallId, hash, access, ct);
    }

    /// <summary>
    /// The bytes of a buffered file, checked through <see cref="FileHashMemo"/> (hashed once per file version); null
    /// when the file is gone, unreadable, or no longer hashes to the asset's sha.
    /// </summary>
    private async Task<byte[]?> ReadFileAsync(ResolvedAsset asset, string path, CancellationToken ct)
    {
        try
        {
            var bytes = await fileHashes.ReadVerifiedAsync(path, asset.Sha256, ct);
            if (bytes is null && File.Exists(path))
            {
                LogMismatch(asset);
            }

            return bytes;
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

    private void LogMismatch(ResolvedAsset asset) =>
        logger.LogWarning("{Kind} {RowId} no longer hashes to {Sha}; refusing to serve it under that hash", asset.Kind, asset.RowId, asset.Hex);
}
