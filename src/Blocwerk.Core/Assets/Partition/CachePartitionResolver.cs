// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Assets.Partition;

/// <summary>
/// The caller's cache partition (design §6.6, S5a), for <c>/api/me/cache-partition</c> and the <c>bw-partition</c> meta.
/// It follows the cookie identity:
/// <list type="bullet">
/// <item><description>a kiosk tablet: <c>k-</c> + HMAC of its key and wall, for every identity it takes (anonymous and
/// act-as alike), or <c>none</c> when it may not cache its wall (<see cref="KioskCacheRules"/>);</description></item>
/// <item><description>a signed-in user: <c>u-</c> + HMAC of the user id;</description></item>
/// <item><description>nobody: <c>anon</c>.</description></item>
/// </list>
/// A share token never changes the partition: a share-linked wall is cached under the viewer's own one.
/// </summary>
public sealed class CachePartitionResolver(
    ICachePartitionKey key,
    ICurrentUserService currentUserService,
    IKioskContext? kioskContext = null) : ICachePartitionResolver
{
    /// <summary>The length of the hashed part of a label: 20 base32 characters, 100 bits.</summary>
    public const int LabelLength = 20;

    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    /// <inheritdoc/>
    public async Task<string> ResolveAsync()
    {
        if (kioskContext is not null)
        {
            await kioskContext.InitializeAsync();
            if (kioskContext.IsKiosk)
            {
                return kioskContext.KioskApiKeyId is { } keyId && KioskCacheRules.CachedWallId(kioskContext) is { } wallId
                    ? CachePartitions.KioskPrefix + Label($"kiosk:{keyId:N}:{wallId:N}")
                    : CachePartitions.None;
            }
        }

        try
        {
            var user = await currentUserService.GetCurrentUserAsync();
            return CachePartitions.UserPrefix + Label($"user:{user.Id:N}");
        }
        catch (UnauthorizedAccessException)
        {
            return CachePartitions.Anonymous;
        }
    }

    private string Label(string identity)
    {
        var mac = HMACSHA256.HashData(key.Key, Encoding.UTF8.GetBytes(identity));
        var label = new StringBuilder(LabelLength);
        for (var bit = 0; label.Length < LabelLength; bit += 5)
        {
            var index = bit / 8;
            var window = (mac[index] << 8) | (index + 1 < mac.Length ? mac[index + 1] : 0);
            label.Append(Base32[(window >> (11 - (bit % 8))) & 31]);
        }

        return label.ToString();
    }
}
