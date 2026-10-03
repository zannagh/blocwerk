using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>The "your 3D wall model / photo-real view is ready" notices of the in-app glyph capture.</summary>
public sealed partial class PushNotificationService
{
    public Task NotifyWallModelReadyAsync(Guid wallId, Guid userId) =>
        GuardAsync(nameof(NotifyWallModelReadyAsync), async db =>
        {
            var wallName = await WallNameAsync(db, wallId);
            var payload = new PushPayload(
                Title: wallName,
                Body: $"3D model ready for {wallName}.",
                Url: $"/walls/{wallId}",
                Tag: $"wall-model-{wallId}");

            // Opt-outs are honoured inside the fan-out (WallModelReady bit).
            await SendToUsersAsync([userId], payload, NotificationType.WallModelReady);
        });

    public Task NotifyWallPhotoRealReadyAsync(Guid wallId, Guid userId) =>
        GuardAsync(nameof(NotifyWallPhotoRealReadyAsync), async db =>
        {
            var wallName = await WallNameAsync(db, wallId);
            var payload = new PushPayload(
                Title: wallName,
                Body: $"Photo-real view ready for {wallName}.",
                Url: $"/walls/{wallId}/3d",
                Tag: $"wall-photoreal-{wallId}");

            // Same opt-out bit as the model notice (WallModelReady).
            await SendToUsersAsync([userId], payload, NotificationType.WallModelReady);
        });
}
